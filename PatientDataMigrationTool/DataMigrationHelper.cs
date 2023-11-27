using Azure.Core;
using CSharpFunctionalExtensions;
using Domain.Abstractions;
using Domain.Entities;
using Domain.Interfaces.ExternalClients;
using Domain.ValueObjects;
using Infrastructure.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
        private readonly Dictionary<long, Guid> patientMedicationIdsMapping;
        private readonly Dictionary<long, Guid> adminHoursIdsMapping;

        public DataMigrationHelper(ApplicationDbContext newPatientDbContext, Domain.Interfaces.ExternalClients.IFacilityClient facilityClient, IOldPatientClient oldPatientClient, Domain.Abstractions.ICheckPatientExistService checkPatientExistService)
        {
            _newPatientDbContext = newPatientDbContext;
            _facilityClient = facilityClient;
            _oldPatientClient = oldPatientClient;
            _checkPatientExistService = checkPatientExistService;
            patientMedicationIdsMapping = new Dictionary<long, Guid>();
            adminHoursIdsMapping = new Dictionary<long, Guid>();
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
                int numOfPatientsProcessed= 0;
                var failures = new List<KeyValuePair<string, long>>();

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
                            failures.Add(new KeyValuePair<string, long>( prepareNewPatientResult.Error, patient.PatientId));

                            //if (prepareNewPatientResult.Error.Contains("Patient Must Have At Least One Address"))
                            //    continue;

                            //await Console.Out.WriteLineAsync($"-{numOfPatientsProcessed}- Patient id : {patient.PatientId}, Error during prepare new patient object : {prepareNewPatientResult.Error}");

                            continue;
                        }

                        var saveNewPatientResult = await SaveNewPatientAsync(prepareNewPatientResult.Value);
                        if (saveNewPatientResult.IsFailure)
                        {
                            failures.Add(new KeyValuePair<string, long>(saveNewPatientResult.Error, patient.PatientId));

                            //await Console.Out.WriteLineAsync($"-{numOfPatientsProcessed}- Patient id : {patient.PatientId}, Error saving patient: " + saveNewPatientResult.Error);
                        }
                        else
                        {
                            numOfMigratedPatients++;
                        }
                    }

                    pageNumber++; // Move to the next page
                }

                await Console.Out.WriteLineAsync($"** {numOfPatientsProcessed} patients processed : {numOfMigratedPatients} patients migrated successfully, {failures.Count} patients failed with the following reasons **");

                var failureReasons = failures.GroupBy(x => x.Key).Select(x => new { Reason = x.Key, Count = x.Count(), PatientIds = string.Join(",", x.Select(r => r.Value.ToString())) }).ToList();
                foreach (var failureReason in failureReasons)
                    await Console.Out.WriteLineAsync($"Reason : {failureReason.Reason}, Count : {failureReason.Count}, PatientIds : {failureReason.PatientIds}");

                //print patient medication ids mapping
                await Console.Out.WriteLineAsync($"** Patient Medication Ids Mapping **");
                foreach (var patientMedicationIdMapping in patientMedicationIdsMapping)
                    await Console.Out.WriteLineAsync($"{patientMedicationIdMapping.Key},{patientMedicationIdMapping.Value}");

                //print admin hours ids mapping
                await Console.Out.WriteLineAsync($"** Admin Hours Ids Mapping **");
                foreach (var adminHoursIdMapping in adminHoursIdsMapping)
                    await Console.Out.WriteLineAsync($"{adminHoursIdMapping.Key},{adminHoursIdMapping.Value}");
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
                var gender = Gender.Unknown;
                if (!string.IsNullOrWhiteSpace(patient.Gender))                    
                {
                    try
                    {
                        gender = (Gender)Enum.Parse(typeof(Gender), patient.Gender);
                    }
                    catch (Exception)
                    {
                    }
                }

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
                        null,
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
                else
                    newPatient.SetComment(" ");
                if (!string.IsNullOrEmpty(patient.ProfilePicPath))
                    newPatient.UpdateProfilePic(patient.ProfilePicPath);

                //files
                foreach (var file in patient.PatientFiles)
                {
                    var createDocumentResult = Domain.ValueObjects.Document.Create(file.FileName, file.AzureFilePath);
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

                            var newAdminHourGUID = Guid.NewGuid();
                            newAdminHours.Add(new Domain.Entities.PatientMedicationAdminHour()
                            {
                                Id = newAdminHourGUID,
                                Hour = parsedHour
                            });

                            adminHoursIdsMapping.Add(adminHour.PatientMedicationAdminHourId, newAdminHourGUID);
                        }

                        //payer
                        var billingType = (Domain.Enums.EnumsCollection.EMedicationBillingType?)null;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(medication.Payer))
                                if (Enum.IsDefined(typeof(Domain.Enums.EnumsCollection.EMedicationBillingType), medication.Payer))
                                    billingType = (Domain.Enums.EnumsCollection.EMedicationBillingType)Enum.Parse(typeof(Domain.Enums.EnumsCollection.EMedicationBillingType), medication.Payer);
                        }
                        catch 
                        {
                        }

                        var newMedicationResult = await Domain.Entities.PatientMedication.Create(newPatientFacilityId, medication.OrderNumber, medicalInfoResult.Value, rxInfoResult.Value, billingType, newAdminHours, newPatient, null);
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

                        patientMedicationIdsMapping.Add(medication.PatientMedicationId, newMediation.Id);

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

        public async Task MigrateAllergiesAsync()
        {
            try
            {
                var allergiesListToAdd = new List<string>() { "Duratuss ac, Lactose intolerance (gi), and Tramadol", "Elavil", "Bimatoprost", "Ciprofloxacin", "Iodine containing compounds ", "Sulfa, Tetracycline ", "Acarbose", "ACE Inhibitors", "Acetaminophen ", "Acetominophen", "Acyclovir", "adhesive tape", "Alcohol", "Almond oil", "ALOE VERA", "Alprazolam", "Altace", "Ambien", "amitiptyline", "amitriptyline", "AMLODIPINE", "Amlodipine besylate", "amoxcillin", "Amoxicillin", "ampicillin", "Ancef", "Antibiotics (sulfa drugs), Aspirin (NSAIDs), Ibuprofen, Naprosyn", "antivert", "Aricept", "ASA", "aspirin", "Aspirin (NSAIDs)", "Aspirin 81mg", "Aspirin-oxycodone", "atenolol", "Ativan", "Atorvastatin", "Atorvastatin calcium", "Atrovastatin", "augmentin", "Avocado", "Azythromyicn", "Bacitracin", "Baclofen", "BACTRIM", "Balsam of Peru", "Banana", "Barbiturates", "Bee pollen ", "Bee sting", "Bee venom", "Beef", "Beer", "BENADRYL", "Benzathine 600.000unit/ml IM", "Benzoyl Peroxide", "BETA-BLOCKERS", "Black beans", "BLEACH", "Brocoli", "Bupropion", "Buspar", "Byetta", "Caffeine", "Carbamazepine", "Cardura", "Carisoprodol", "Carrot", "Carvedilol", "cashews, pine nuts, pistachios", "Cats", "Ceclor", "Cefalexin", "Cefazolin", "celecobix", "Celecoxib", "Celery", "cephalexin", "CEPHALOSPORINS", "cheese", "Chicken", "Chilli", "Chlorhexidine", "Chlorthalidone", "Chocolate", "Chromium", "Cigarette smoke", "Cipro", "ciproflax", "Ciprofloxacin", "Ciprofloxacin, Penicillin", "Citalopram", "Claritin", "Cleaning products", "Clindamycin ", "Clonidine", "Cobalt", "Cockroaches", "Codeine", "Codeine Phosphate", "Codeine sulfate", "Codeine, Lisinopril, Amlodipine, Benazepril, Acetaminophen", "CODIENE", "Cogentin", "Compazine", "Contrast Dye", "contrast dye , iodine", "Corn", "Cortisone", "cortisone (unknown reaction)", "Cosmetics", "COVID 19 Vaccine MODERNA", "COX-2 inhibitor ", "cozaar", "Cuvar Inhaler ", "cyclosporins", "Cymbalta", "Darvocet", "Demerol", "Depakote", "diazepam", "DICLOFENAC ", "Dilantin", "DILANTIN [PHENYTOIN]", "Dilaudid", "Dimethylaminopropylamine (DMAPA)", "Diovan", "Diphendyramine", "Diuretics", "Dogs", "Donepezil", "doxylamine", "dramamine", "Droperidol", "Dulaglutide", "duloxetine", "Dust mites", "DYE", "Ear drops", "ECOTRIN", "Egg", "Egg white", "Enalapril Maleate", "Environmental allergies", "Environmental allergy", "Epinephrine", "Erthromycin", "Erythomycin, Morphine, Penicillin (Swelling), Sulfadiazine", "Erythromycin", "ERYTHROMYCIN BASE", "FELODIPINE", "fenobrate", "Fentanyl", "Ferrous Sulfate", "figs", "Fish", "Fish-Products", "fishproduct", "FLAGYL", "Flexeril", "Flomax", "Food dye", "Formaldehyde", "fosamax", "Fosaprepitant", "Fosinopril", "Fruit juices with dyes", "Fungicide", "Gabapentin", "Gabapentin, Dorzolamide, NSAIDS, Ceftriaxone", "Garlic", "Gelatin", "gemfibrozil", "Glipizide", "Gluten", "Glyceryl monothioglycolate", "Gold", "Grapes", "Grass", "Hair dye", "HALDOL", "Haloperidol ", "Hay Fever", "HCTZ", "HEPARIN", "HYDRALAZINE", "Hydrochlorothiazide", "Hydrocodone", "Ibuprofen", "Imuran", "Inaspine", "indomethcin", "Influenza virus vaccines", "Insect sting", "interferon", "Intravenous contrast dye", "Iodinated contrast media", "IODINE", "Iodine (Topical)", "Iodine based contrast media", "IODINE-BASED CONTRAST MEDIA", "iodine-basedcontrastmedia", "ipratropium", "IV Contrast", "KEFLEX", "Ketoconazole", "KETOROLAC", "Kiwi", "Lactate", "Lactose", "LACTOSE INTOLERANCE", "lamotrigine", "Lasix", "Latex", "lentils", "Levaquen Leva PAK", "levaquin", "Levofloxacin", "Levonorgestrel-ethynilEstrad", "Lexapro", "Lidocaine", "Limbrel", "LINDANE", "Lipitor", "Lisinopril", "Lithium", "Live Allergy", "Local anesthetics", "Lorazepam ", "LOVAST", "LOVASTATIN", "Lupin", "Macrobid", "MACROLIDES", "Mango", "MEDI-HONEY", "melixocam, prednisone", "Melon", "Meperidine", "Metformin", "Methadone", "Metoclopamide", "metoprolol", "Metoprolol Tartrate, Pravastatin Sodium, Fosamax, Zocor, Bactrim", "Metronidazole", "Midazolam", "Milk", "MIRTAZAPINE", "Mold", "Molluscs", "MOLLUSKS", "MOLLUSKS ( SCALLOPS, CLAMS, OYSTERS )", "morphine", "Morphine(confusion)", "Motrin", "Mucinex", "Mustard", "Nail polish", "naloxone", "NAPROSYN", "Naproxen", "Narco", "Neomycin", "NEOSPORIN", "Neurontin", "niacin", "Nickel", "Nifedipine", "NITROFURAN DERIVATIVES", "Nitrofurantoin", "Nitroglycerin (hypotension)", "NKA", "NKDA", "No Allergies", "No known allergies", "No Known Allergy", "NOKNOWN", "NoKnownAllergies", "Norvasc", "Novocain", "nsaid", "NSAiDS", "Nuts", "NYSTATIN", "Oats", "Ofloxacin", "OLANZEPINE", "OMEPRAZOLE", "omnipaque", "onions", "Opiate Derivatives", "opioid-like analgesics", "Opioids", "Opium", "Oranges", "Oxacarbazepine", "Oxacillin", "Oxcarbazepine", "oxybutin", "oxybutynin", "Oxycodone", "OXYCODONE-ACETAMINOPHEN", "oxymetazoline", "Pantoprazole, Alke-Seltzer antacid, Cheese, Pork, shrimp", "Paradol", "Paraphenylenediamine (PPD)", "PAROXETINE", "PAXIL", "PCN", "PCN, SARS-CoV-2 (COVID-19) mRNA-1273 vaccine", "PCN=SOB", "PCNs", "Peach", "Peanut", "peas", "PENCICLOVIR ", "PENIC", "Peniciilin", "PENICILIN", "penicillin", "Penicillin G ", "Penicillin G Benzathine", "Penicillin Notatum", "Penicillin V potassium", "Penicillins", "penicillins, amoxicillin, demerol, morphine, fentanyl, gabapentin, propoxyphene, dilaudid, hydrocodone ", "Pepper", "PERCOCET", "Percodan", "Perfume", "persimmon fruit", "Pet dander", "PHENobarbital", "Phenylephrine CM", "pine nuts", "Pineapple", "Pioglitazone HCL", "Plum", "Pneumococcal vac polyvalent", "Pollens", "POLYSORBATE", "Pork", "Potassium Chloride", "Poultry meat", "pradaxa", "PRAVASTATIN", "Pravastatin sodium", "Precedex", "Prednisone", "Pregabalin", "PRINIVIL", "prinzide", "Prochlorperazine", "Prolixin", "Propanolol", "Propofol", "Propoxyphene", "Prosac", "Prozac", "psyllium", "Quinine, Tetracycline, Pravachol", "QUINOLONES", "RBCs antibodies ", "Red dye", "Red meat", "reglan", "restasis", "Risedronate sodium", "risperidone", "ROCEPHIN", "Rosuvastatin", "SAIDs", "Salmetrol ", "Scopolamine", "Seafood", "Seasonal", "Seeds", "Semen", "Septra", "Seroquel", "Seroquel(tongueswelling)", "Seroquel/tongue swelling", "Sesame", "Shell Fish", "Shellfish", "Simethicone", "SIMVASTATIN", "Sitagliptin", "Soap/ Shampoo", "SODIUM THIOSULFATE", "sohail.gu11@curenta.com", "solifenacin", "Solumedrol", "Sotalol", "Soy", "Spices", "Squash", "SSRI drugs", "stadol", "statins", "Stelazine", "Steroids", "Strawberries", "Streptomycin", "sudafed", "SULFA", "Sulfa (Sulfonamide antibiotics) ", "Sulfa Antibiotics", "sulfa antibotics", "Sulfa Drugs ", "Sulfa, Ciprofloxacin", "SULFAANTIBIOTICS", "sulfadrugs(unknown)", "Sulfamethoxazole ", "Sulfamethoxazole, Trimethoprim, Morphine Sulfate ", "Sulfamethoxazole-trimethoprim", "Sulfate", "Sulfites", "Sulfur", "sulindoc", "Sumatriptan", "Sun", "tagament", "Tamsulosin", "Tape", "Tartrazine", "TDP vaccine", "Tegretol", "Terazosin", "Test SG Allergy......", "test_ibram", "TETANUS", "Tetracycline ", "Tetracycline Hydrochloride ", "THIO PENTAL", "Thorazine", "Timolol", "Toluidine", "Tomato", "Topiramate", "toradol", "tositumomab", "toxoid", "Tramadol", "Trazodone ", "Trazodone-hydrochloride ", "Tree nuts", "Trees", "Trental", "TRETINOIN", "TRIMETHOPRIN", "Tylenol", "Tylenol #3", "ULORIC", "Vancomycin", "Venlafaxine HCL", "Verapamil", "Vesicare", "Vicodin", "Voltaren", "walnuts", "Warfarin", "Water", "Wellbutrin", "Wheat", "WHEY", "Xanax", "ZITHROMAX", "zofran", "Zoloft", "zolpidem", "Zosyn" };

                foreach (var allergy in allergiesListToAdd)
                {
                    var alreadyExist = _newPatientDbContext.Allergies.FirstOrDefault(x => x.AllergyDesc == allergy);
                    if (alreadyExist != null)
                        continue;

                    var createAllergiesResult = Domain.Entities.Allergies.Create(allergy);
                    if (createAllergiesResult.IsFailure)
                        throw new Exception(createAllergiesResult.Error);
                    var allergies = createAllergiesResult.Value;
                    _newPatientDbContext.Allergies.Add(allergies);
                }
                await _newPatientDbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}
