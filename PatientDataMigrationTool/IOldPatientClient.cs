using PatientsCoreAPI.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PatientDataMigrationTool
{
    internal interface IOldPatientClient
    {
        Task<CSharpFunctionalExtensions.Result<(List<Patient> Patients, List<PatientMedication> PatientMedications)>> GetPatients(int? PageSize, int? PageNumber);
    }
}
