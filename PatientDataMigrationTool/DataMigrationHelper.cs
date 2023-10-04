using CSharpFunctionalExtensions;
using Domain.Abstractions;
using Domain.Entities;
using Domain.Interfaces.ExternalClients;
using Domain.ValueObjects;
using Infrastructure.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static CurentaDomain.Enums.EnumsCollection;

namespace PatientDataMigrationTool
{
    internal class DataMigrationHelper
    {
        private ApplicationDbContext _newPatientDbContext;
        private IFacilityClient _facilityClient;
        private IOldPatientClient _oldPatientClient;
        private readonly ICheckPatientExistService _checkPatientExistService;
        public DataMigrationHelper(ApplicationDbContext newPatientDbContext, Domain.Interfaces.ExternalClients.IFacilityClient facilityClient, IOldPatientClient oldPatientClient, Domain.Abstractions.ICheckPatientExistService checkPatientExistService)
        {
            _newPatientDbContext = newPatientDbContext;
            _facilityClient = facilityClient;
            _oldPatientClient = oldPatientClient;
            _checkPatientExistService = checkPatientExistService;
        }

        internal async Task MigrateAsync()
        {
            try
            {
                int pageSize = 200; // Set the desired page size
                int pageNumber = 1; // Initialize the page number

                await Console.Out.WriteLineAsync("Migrate in batches (y/n) ?");
                var batchAnswer = string.Empty;
                batchAnswer = Console.ReadLine();
                if(batchAnswer != null && batchAnswer.ToLower() == "y") 
                {
                    await Console.Out.WriteLineAsync("Provide batch size : ");
                    var batchSizeAnswer = string.Empty;
                    batchSizeAnswer = Console.ReadLine();
                    pageSize = int.Parse(batchSizeAnswer);
                }

                int numOfMigratedPatients = 0;
                int numOfFailedPatients = 0;
                int numOfPatientsProcessed= 0;

                while (true)
                {
                    await Console.Out.WriteLineAsync($"Getting data of {pageSize} patients");
                    var patientsResult = await _oldPatientClient.GetPatients(pageSize, pageNumber);
                    if (patientsResult.IsFailure)
                    {
                        Console.WriteLine("Error fetching patients: " + patientsResult.Error);
                        break; // Exit the loop if there's an error
                    }

                    var patients = patientsResult.Value.Patients;
                    var medications = patientsResult.Value.PatientMedications;

                    if (patients.Count == 0)
                    {
                        // No more patients to process
                        break;
                    }

                    foreach (var patient in patients)
                    {
                        numOfPatientsProcessed++;

                        var patientMedications = medications.Where(x => x.PatientIdRef == patient.PatientId).ToList();

                        var prepareNewPatientResult = await PrepareNewPatientAsync(patient, patientMedications);
                        if (prepareNewPatientResult.IsFailure)
                        {
                            //if (prepareNewPatientResult.Error.Contains("Patient Must Have At Least One Address"))
                            //    continue;

                            await Console.Out.WriteLineAsync($"-{numOfPatientsProcessed}- Patient id : {patient.PatientId}, Error during prepare new patient object : {prepareNewPatientResult.Error}");
                            //throw new Exception(prepareNewPatientResult.Error);
                            numOfFailedPatients++;
                            continue;
                        }

                        var saveNewPatientResult = await SaveNewPatientAsync(prepareNewPatientResult.Value);
                        if (saveNewPatientResult.IsFailure)
                        {
                            await Console.Out.WriteLineAsync($"-{numOfPatientsProcessed}- Patient id : {patient.PatientId}, Error saving patient: " + saveNewPatientResult.Error);
                            numOfFailedPatients++;
                            //throw new Exception(saveNewPatientResult.Error);
                        }
                        else
                        {
                            await Console.Out.WriteLineAsync($"-{numOfPatientsProcessed}- Patient id : {patient.PatientId} migrated successfilly");
                            numOfMigratedPatients++;
                        }
                    }

                    pageNumber++; // Move to the next page
                }

                await Console.Out.WriteLineAsync($"** {numOfPatientsProcessed} patients processed : {numOfMigratedPatients} patients migrated successfully, {numOfFailedPatients} patients failed **");

            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }


        private async Task<Result<Domain.Entities.Patient>> PrepareNewPatientAsync(PatientsCoreAPI.Infrastructure.Models.Patient patient, List<PatientsCoreAPI.Infrastructure.Models.PatientMedication> patientMedications)
        {
            try
            {
                Domain.Entities.Patient newPatient;

                //patient basic data with addresses and place of service
                var gender = new Domain.Entities.Gender();
                Enum.TryParse(patient.Gender, out gender);

                var basicInfoResult = Domain.Entities.PatientBasicInfo.Create(
                    patient.Fname,
                    patient.Lname,
                    patient.Email,
                    patient.Phonenumber,
                    DateOnly.FromDateTime(DateTime.Parse(patient.Dob)),
                    gender
                    );
                if (basicInfoResult.IsFailure)
                    throw new Exception(basicInfoResult.Error);

                var addressess = new List<Domain.Entities.Address>();
                foreach (var address in patient.PatientAddresses)
                {
                    var addressType = new AddressType();
                    if (string.IsNullOrEmpty(address.AddressType))
                        addressType = AddressType.Other;
                    else
                    {
                        if (address.AddressType.ToLower().Contains("assisted"))
                            addressType = AddressType.AssistedLivingFacility;
                        else if (address.AddressType.ToLower().Contains("board"))
                            addressType = AddressType.BoardAndCare;
                        else if (address.AddressType.ToLower().Contains("residential"))
                            addressType = AddressType.Residential;
                        else if (address.AddressType.ToLower().Contains("skilled"))
                            addressType = AddressType.SkilledNursingFacility;
                        else if (address.AddressType.ToLower().Contains("other"))
                            addressType = AddressType.Other;
                    }

                    var newAddressCreateResult = Domain.Entities.Address.Create(
                        address.Address,
                        address.Address,
                        !string.IsNullOrWhiteSpace(address.Street) ? address.Street : "-",
                        !string.IsNullOrWhiteSpace(address.City) ? address.City : "-",
                        !string.IsNullOrWhiteSpace(address.State) ? address.State : "CA",
                        !string.IsNullOrWhiteSpace(address.ZipCode) ? address.ZipCode : "-",
                        addressType,
                        address.Lng,
                        address.Lat,
                        address.Address == patient.DeliveryAddress ? true : false,
                        false,
                        false,
                        address.IsDefault != null && address.IsDefault == true ? true : false
                        );

                    if (newAddressCreateResult.IsFailure)
                        throw new Exception("Address : " + newAddressCreateResult.Error);

                    addressess.Add(newAddressCreateResult.Value);
                }

                var patientStatus = new Domain.Entities.PatientStatus();
                if (string.IsNullOrWhiteSpace(patient.PatientStatus))
                    patientStatus = PatientStatus.InActive;
                else
                    Enum.TryParse(patient.PatientStatus.Trim(), out patientStatus);

                long? newPatientFacilityId = null;

                if ((patient.FacilityIdRef == null || patient.FacilityIdRef == 0) && patient.Facility == null)
                {
                    var createPtientResult = await Domain.Entities.Patient.CreateRetailPatient(_checkPatientExistService,basicInfoResult.Value, addressess, patientStatus);
                    if (createPtientResult.IsFailure)
                        throw new Exception(createPtientResult.Error);
                    newPatient = createPtientResult.Value;
                }
                else
                {
                    newPatientFacilityId = patient.FacilityIdRef != null && patient.FacilityIdRef != 0 ? (long)patient.FacilityIdRef : (long)patient.Facility.Id;
                    var facilityCreateResult = Domain.Entities.PlaceOfServiceDetails.Create(
                            (long)newPatientFacilityId,
                            patient.PatientResidential != null && patient.PatientResidential.WingId != null ? patient.PatientResidential.WingId.ToString() : null,
                            patient.PatientResidential != null && !string.IsNullOrEmpty(patient.PatientResidential.Room) ? patient.PatientResidential.Room : null,
                            patient.NurseIdRef != null ? patient.NurseIdRef.ToString() : null,
                            LocationOfService.Facility
                        );
                    if (facilityCreateResult.IsFailure)
                        throw new Exception(facilityCreateResult.Error);

                    var createPtientResult = await Domain.Entities.Patient.CreateFacilityPatient(_checkPatientExistService, basicInfoResult.Value, addressess, patientStatus, facilityCreateResult.Value);
                    if (createPtientResult.IsFailure)
                        throw new Exception(createPtientResult.Error);
                    newPatient = createPtientResult.Value;
                };

                //personal info
                string? resuscitation = null;
                if (patient.PatientResidential != null)
                {
                    if (patient.PatientResidential.Resuscitation != null)
                        resuscitation = patient.PatientResidential.Resuscitation.ResuscitationName;
                    else if (patient.PatientResidential.ResuscitationId != null)
                        resuscitation = ((EResuscitation)Enum.ToObject(typeof(EResuscitation), patient.PatientResidential.ResuscitationId)).ToString();
                    else if (patient.PatientResidential.ResuscitationDisplayValue != null)
                        resuscitation = patient.PatientResidential.ResuscitationDisplayValue.ToString();
                }

                var personalInfoResult = PatientPersonalInformation.Create(
                        patient.SocialSecurityNumb,
                        patient.Mrnumber,
                        patient.MainDiagnosis,
                        patient.PatientResidential?.Diet,
                        patient.PatientAllergies != null ? patient.PatientAllergies.Select(a => a.AllergyDesc).ToList() : null,
                        resuscitation
                        );
                if (personalInfoResult.IsFailure)
                    throw new Exception(personalInfoResult.Error);
                newPatient.UpdatePersonalInfo(personalInfoResult.Value);

                //todo other fields

                if (patient.IsBp.HasValue)
                    if (patient.IsBp == true)
                        newPatient.EnableBubblePack();
                    else
                        newPatient.DisableBubblePack();

                if (!string.IsNullOrEmpty(patient.DeliveryNote))
                    newPatient.SetDeliveryNote(patient.DeliveryNote);
                else
                    newPatient.SetDeliveryNote(" ");
                if (!string.IsNullOrEmpty(patient.Comments))
                    newPatient.SetComment(patient.Comments);
                if (!string.IsNullOrEmpty(patient.ProfilePicPath))
                    newPatient.UpdateProfilePic(patient.ProfilePicPath);

                //files
                foreach (var file in patient.PatientFiles)
                {
                    var createDocumentResult = Domain.ValueObjects.Document.Create(file.AzureFilePath);
                    if (createDocumentResult.IsFailure)
                        throw new Exception(createDocumentResult.Error);
                    newPatient.AddDocument(createDocumentResult.Value);
                }

                //notes
                foreach (var note in patient.PatientNotes)
                {
                    var createNoteResult = Note.Create(note.Title, note.Body);
                    if (createNoteResult.IsFailure)
                        throw new Exception(createNoteResult.Error);
                    newPatient.AddNote(createNoteResult.Value);
                }

                //medications
                if(patientMedications != null)
                {
                    foreach (var medication in patientMedications)
                    {
                        var medicalInfoResult = PatientMedicationMedicalInfo.Create(
                                medication.TherapCode,
                                medication.HowtoUse,
                                medication.MedName,
                                medication.Ndc,
                                medication.DispensableGenericId,
                                medication.DispensableGenericDesc,
                                medication.DispensableDrugId,
                                medication.DispensableDrugDesc,
                                medication.DispensableDrugTallManDesc,
                                medication.MedStrength,
                                medication.MedStrengthUnit,
                                medication.Indication,
                                medication.IscomfortKit,
                                medication.ComfortKitType,
                                medication.ComfortKit,
                                medication.GenericDrugNameCode,
                                medication.GenericDrugNameCodeDesc,
                                medication.MedicineDisplayName,
                                medication.MedicineNameSaving,
                                medication.Comments
                            );
                        if (medicalInfoResult.IsFailure)
                            throw new Exception(medicalInfoResult.Error);

                        var rxInfoResult = PatientMedicationRXInfo.Create(
                                (!string.IsNullOrEmpty( medication.Directions) ? medication.Directions : (!string.IsNullOrEmpty(medication.Frequency) ? medication.Frequency : " " )),
                                (!string.IsNullOrEmpty(medication.Frequency) ? medication.Frequency : " "),
                                null,
                                medication.Route,
                                medication.Quantity,
                                medication.Dosage,
                                medication.DoseFormId,
                                medication.DoseFormDesc,
                                medication.NumberOfRefillsAllowed,
                                medication.NumberOfRefillsRemaining,
                                medication.NextRefillDate != null ? DateOnly.FromDateTime(medication.NextRefillDate.Value) : null,
                                medication.StartDate != null ? DateOnly.FromDateTime(medication.StartDate.Value) : null,
                                medication.EndDate != null ? DateOnly.FromDateTime(medication.EndDate.Value) : null,
                                null,
                                null,
                                medication.Iscycle,
                                medication.Isdaw,
                                medication.Isprn
                            );
                        if (rxInfoResult.IsFailure)
                            throw new Exception(rxInfoResult.Error);

                        var newAdminHours = new HashSet<Domain.Entities.PatientMedicationAdminHour>();
                        foreach (var adminHour in medication.AdminHours)
                        {
                            var parsedHour = TimeOnly.ParseExact(adminHour.Hour, "hh:mm tt", CultureInfo.InvariantCulture);

                            newAdminHours.Add(new Domain.Entities.PatientMedicationAdminHour()
                            {
                                Hour = parsedHour
                            });
                        }

                        var newMedicationResult = await Domain.Entities.PatientMedication.Create(newPatientFacilityId, medication.OrderNumber, medicalInfoResult.Value, rxInfoResult.Value, newAdminHours, newPatient, null);
                        if (newMedicationResult.IsFailure)
                            throw new Exception(newMedicationResult.Error);

                        var newMediation = newMedicationResult.Value;

                        if (medication.PatientMedicationStatusId == (int)EPatientMedicationStatus.OnHold)
                        {
                            var changeStatusResult = newMediation.ChangeStatusForMigration(Domain.Enums.EnumsCollection.EPatientMedicationStatus.OnHold, !string.IsNullOrEmpty( medication.DiscontinuationReason) ? medication.DiscontinuationReason : " ", newPatient);
                            if (changeStatusResult.IsFailure)
                                throw new Exception(changeStatusResult.Error);
                        }
                        if (medication.PatientMedicationStatusId == (int)EPatientMedicationStatus.Discontinued)
                        {
                            var changeStatusResult = newMediation.ChangeStatusForMigration(Domain.Enums.EnumsCollection.EPatientMedicationStatus.Discontinued, !string.IsNullOrEmpty(medication.DiscontinuationReason) ? medication.DiscontinuationReason : " ", newPatient);
                            if (changeStatusResult.IsFailure)
                                throw new Exception(changeStatusResult.Error);
                        }

                        //shadow properties
                        if (medication.CreateDate != null)
                            newMediation.SetCreatedDate(medication.CreateDate.Value);
                        if (medication.UpdateDate != null)
                            newMediation.SetUpdatedDate(medication.UpdateDate.Value);

                        var addMedicationResult = await newPatient.AddMedicationForMigration(newMediation, newPatientFacilityId, _facilityClient);
                    }
                }

                //external id
                var addExternalIdResult = newPatient.AddExternalId(new ExternalId(patient.PatientId.ToString(), ExternalSystem.CurentaOldSystem));
                if (addExternalIdResult.IsFailure)
                    throw new Exception(addExternalIdResult.Error);

                //shadow properties
                if (patient.CreateDate != null)
                    newPatient.SetCreatedDate(patient.CreateDate.Value);
                if (patient.UpdateDate != null)
                    newPatient.SetUpdatedDate(patient.UpdateDate.Value);
                if (patient.UpdateBy != null)
                    newPatient.SetUpdatedBy(patient.UpdateBy.Value);

                return newPatient;
            }
            catch (Exception ex)
            {
                return Result.Failure<Domain.Entities.Patient>(ex.Message);
                //throw new Exception($"Patient id : {patient.PatientId}, Error : {ex.Message}");
            }

        }

        private async Task<Result> SaveNewPatientAsync(Patient patient)
        {
            try
            {
                _newPatientDbContext.Patients.Add(patient);
                var saveResult = await _newPatientDbContext.SaveChangesAsync();
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Failure(ex.Message + " - " + ex.InnerException?.Message);
            }
        }
    }
}
