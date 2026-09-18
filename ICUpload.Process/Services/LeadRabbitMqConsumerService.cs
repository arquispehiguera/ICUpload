using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog.Context;
using ICUpload.Core.Entities;
using ICUpload.Core.Interfaces;
using ICUpload.Infraestructure;

namespace ICUpload.Process.Services
{
    // Consume la cola "Lead" (ya declarada en el broker, con su DLX apuntando a "LeadFail") y
    // corre el flujo completo de LeadService por mensaje. Mismo espíritu que
    // CtiInteraccionBackgroundService (scope por mensaje, LogContext.PushProperty), pero
    // consumo push vía RabbitMQ.Client 7.x (API async-first) en vez de polling SQL.
    public class LeadRabbitMqConsumerService : BackgroundService
    {
        private const string QueueName = "Lead";

        private readonly ILogger<LeadRabbitMqConsumerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;

        public LeadRabbitMqConsumerService(ILogger<LeadRabbitMqConsumerService> logger, IServiceProvider serviceProvider, IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new ConnectionFactory
            {
                HostName = _configuration["RabbitMq:HostName"] ?? "localhost",
                UserName = _configuration["RabbitMq:UserName"] ?? throw new InvalidOperationException("RabbitMq:UserName no configurado"),
                Password = _configuration["RabbitMq:Password"] ?? throw new InvalidOperationException("RabbitMq:Password no configurado"),
                VirtualHost = _configuration["RabbitMq:VirtualHost"] ?? "/",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            await using var connection = await factory.CreateConnectionAsync(stoppingToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

            // QueueDeclarePassive en vez de QueueDeclare: las colas Lead/LeadFail ya existen
            // en el broker (con su DLX configurado) — si no están como se espera, esto falla
            // rápido y explícito en vez de intentar (re)crearlas con la configuración que sea.
            await channel.QueueDeclarePassiveAsync(QueueName, stoppingToken);

            var prefetchCount = (ushort)_configuration.GetValue<int>("Lead:PrefetchCount", 1);
            // global:false → el límite de prefetch aplica por consumer, no por canal completo.
            await channel.BasicQosAsync(0, prefetchCount, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, ea) => HandleMessageAsync(channel, ea, stoppingToken);

            await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

            _logger.LogInformation("LeadRabbitMqConsumerService escuchando la cola {Queue} (PrefetchCount={Prefetch})", QueueName, prefetchCount);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown normal del host — nada que loguear como error.
            }
        }

        private async Task HandleMessageAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken hostCt)
        {
            var json = Encoding.UTF8.GetString(ea.Body.ToArray());

            // Deserializar con Newtonsoft → si falla o da null, Nack directo sin pasar por
            // Polly (no tiene sentido reprocesar un mensaje que nunca fue válido).
            LeadMessage? message;
            try
            {
                message = JsonConvert.DeserializeObject<LeadMessage>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mensaje de Lead no deserializable — Nack directo, sin reintentos. Payload: {Json}", json);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                return;
            }

            if (message is null)
            {
                _logger.LogError("Mensaje de Lead deserializado a null — Nack directo, sin reintentos. Payload: {Json}", json);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                return;
            }

            using (LogContext.PushProperty("ContactId", message.ContactId))
            using (LogContext.PushProperty("Campaign", message.Campaign))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var leadService = scope.ServiceProvider.GetRequiredService<ILeadService>();

                    // Se pasa hostCt al propio ExecuteAsync (no solo a ProcessAsync) para que
                    // los delays de WaitAndRetryAsync entre reintentos también sean cancelables
                    // — si el host se apaga en medio de un delay, Polly corta ahí en vez de
                    // esperar el segundo completo para recién después notar la cancelación.
                    var result = await LeadResiliencePolicies.LeadFlowRetry.ExecuteAsync(token => leadService.ProcessAsync(message, token), hostCt);

                    if (result.Success)
                    {
                        _logger.LogInformation("Lead procesado correctamente: {Message}", result.Message);
                        await channel.BasicAckAsync(ea.DeliveryTag, false);
                    }
                    else
                    {
                        _logger.LogWarning("Lead no procesado (tras reintentos si correspondía) — Nack a LeadFail: {Message}", result.Message);
                        await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                    }
                }
                catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
                {
                    // Shutdown del host durante el procesamiento de este lead — LeadService ya
                    // dejó propagar la cancelación (no la convirtió en LeadResult) y
                    // LeadResiliencePolicies ya excluyó OperationCanceledException de sus
                    // reintentos, así que si llegamos acá es shutdown real, no un fallo de
                    // negocio del lead. Se reencola (requeue:true) para que otro worker lo
                    // retome más tarde, en vez de mandarlo a LeadFail permanentemente.
                    _logger.LogWarning("Procesamiento de Lead cancelado por shutdown del host — Nack con requeue para reintento por otro worker");
                    await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true);
                }
                catch (Exception ex)
                {
                    // Backstop defensivo: LeadService.ProcessAsync no debería lanzar nunca,
                    // pero una falla al resolver el scope/DI sí podría llegar hasta acá.
                    _logger.LogError(ex, "Excepción no controlada procesando Lead — Nack a LeadFail");
                    await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                }
            }
        }
    }
}
