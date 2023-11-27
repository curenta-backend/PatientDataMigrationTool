using Domain.Abstractions;
using Domain.Interfaces.ExternalClients;
using Infrastructure.DB;
using Infrastructure.ExternalClients;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PatientDataMigrationTool;
using Patients.Mappings;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, type Y to start data migration : ");
        var input = Console.ReadLine();
        if (input.ToLower() == "y")
        {
            Console.WriteLine("Starting data migration");

            var services = new ServiceCollection();

            var configuration = new ConfigurationBuilder().Build();

            services.AddHttpClient();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddScoped<IFacilityClient, FacilityClient>();
            services.AddScoped<IOldPatientClient, OldPatientClient>();
            services.AddScoped<ICheckPatientExistService, CheckPatientExistService>();

            MappingConfig.RegisterMappings();

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer("Server=tcp:curenta.database.windows.net,1433;Initial Catalog=patientv2_test;Persist Security Info=False;User ID=curenta;Password=Theexodus#3;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"));

            // Build the service provider
            var serviceProvider = services.BuildServiceProvider();

            // Resolve your DbContext
            using (var scope = serviceProvider.CreateScope())
            {
                var newDbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
                var facilityClient = serviceProvider.GetRequiredService<IFacilityClient>();
                var newPatientClient = serviceProvider.GetRequiredService<IOldPatientClient>();
                var checkPatientExistService = serviceProvider.GetRequiredService<ICheckPatientExistService>();

                var patientDataMigration = new DataMigrationHelper(newDbContext, facilityClient, newPatientClient, checkPatientExistService);

                await patientDataMigration.MigrateAllergiesAsync();

                await patientDataMigration.MigrateAsync();
            }

            Console.WriteLine("Data migration completed");
        }
        else
        {
            Console.WriteLine("Data migration cancelled");
        }
    }
}