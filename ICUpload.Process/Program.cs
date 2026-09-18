using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using PacificoSeguros.Core.Interfaces;
using PacificoSeguros.Infraestructure.Data;
using PacificoSeguros.Infraestructure.Notifications;
using PacificoSeguros.Infraestructure.Repositories;
using PacificoSeguros.Infraestructure.Services;
using PacificoSeguros.Process.Services;

namespace PacificoSeguros.Process
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string basePath;
            if (Debugger.IsAttached)
            {
                basePath = AppContext.BaseDirectory;
                Console.WriteLine($"🧩 Ejecutando en modo desarrollo: {basePath}");
            }
            else
            {
                basePath = @"C:\JobsDeployment\PacificoSegurosProcess";
            }

            // Serilog.Sinks.File resuelve rutas relativas contra el directorio de
            // trabajo del proceso — un Windows Service no necesariamente arranca con
            // un CWD útil (puede ser C:\Windows\System32). Fijarlo acá reusa el mismo
            // basePath ya calculado, sin duplicar la lógica dev/prod en appsettings.json.
            Environment.CurrentDirectory = basePath;

            var configuration = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                // Secretos reales de este entorno (connection strings, credenciales de
                // Inconcert) viven acá, gitignoreado — appsettings.json solo tiene
                // placeholders para poder commitearse.
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "PacificoSegurosProcess")
                .CreateLogger();

            try
            {
                Log.Information("Iniciando Lead Processor...");
                var host = CreateHostBuilder(args, configuration).Build();
                await host.RunAsync();
                Log.Information("Aplicación detenida correctamente");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error fatal al iniciar o ejecutar el host");
                Log.CloseAndFlush();
                // Código de salida != 0: señal inequívoca para el SCM de que hay que
                // aplicar la política de Recovery — no reintentamos acá adentro.
                Environment.Exit(1);
            }

            Log.CloseAndFlush();
        }

        private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(options => options.ServiceName = "PacificoSegurosProcess")
                .UseSerilog()
                .ConfigureAppConfiguration((_, builder) => builder.AddConfiguration(configuration))
                .ConfigureServices((_, services) =>
                {
                    services.Configure<HostOptions>(o =>
                    {
                        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
                        o.ShutdownTimeout = TimeSpan.FromSeconds(15);
                    });

                    // DbContextApp expone la conexión fija AppConnection/CN, usada por el
                    // feature de Leads (ConfiguracionCampana/RegistrarFin).
                    services.AddSingleton<DbContextApp>();

                    // Feature Leads (ICUploadLead) — ver WORKER_SPEC.md.
                    services.AddSingleton<IAmbienteConnectionFactory, AmbienteConnectionFactory>();
                    services.AddTransient<ILeadCampaignRepository, LeadCampaignRepository>();
                    services.AddTransient<IInConcertGateway, InConcertGateway>();
                    services.AddTransient<IAlertNotifier, LogAlertNotifier>();
                    services.AddTransient<ILeadService, LeadService>();
                    services.AddHostedService<LeadRabbitMqConsumerService>();
                });
    }
}
