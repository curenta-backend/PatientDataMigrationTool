using CSharpFunctionalExtensions;
using Newtonsoft.Json;
using PatientsCoreAPI.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PatientDataMigrationTool
{
    public class OldPatientClient : IOldPatientClient
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public OldPatientClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<Result<(List<Patient> Patients, List<PatientMedication> PatientMedications)>> GetPatients(int? PageSize, int? PageNumber)
        {
            try
            {


                string requesturl = "https://localhost:44344/GetAllPatients";
                if (PageSize.HasValue && PageNumber.HasValue)
                    requesturl = $"{requesturl}?PageSize={PageSize}&PageNumber={PageNumber}";
                using var client = _httpClientFactory.CreateClient();
                var response = await client.GetAsync(requesturl);

                if (response.IsSuccessStatusCode)
                {
                    // Read the response content
                    string responseContent = await response.Content.ReadAsStringAsync();

                    // Deserialize the response JSON, including both patients and patient medications
                    var result = JsonConvert.DeserializeObject<(List<Patient> Patients, List<PatientMedication> PatientMedications)>(responseContent);
                    return result;
                }
                else
                {
                    Console.WriteLine($"HTTP Error: {response.StatusCode} - {response.ReasonPhrase}");
                    return Result.Failure<(List<Patient> Patients, List<PatientMedication> PatientMedications)>($"Failed to get patients {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                return Result.Failure<(List<Patient> Patients, List<PatientMedication> PatientMedications)>($"Failed to get patients {ex.Message}");
            }
        }

    }
}
