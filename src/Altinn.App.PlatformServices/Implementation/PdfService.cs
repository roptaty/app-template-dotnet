﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Altinn.App.Common.Helpers.Extensions;
using Altinn.App.Common.Models;
using Altinn.App.PlatformServices.Extensions;
using Altinn.App.PlatformServices.Interface;
using Altinn.App.Services.Interface;
using Altinn.App.Services.Models;
using Altinn.Platform.Profile.Models;
using Altinn.Platform.Register.Models;
using Altinn.Platform.Storage.Interface.Models;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Altinn.App.PlatformServices.Implementation
{
    /// <summary>
    /// Class for generating and storing PDF
    /// </summary>
    public class PdfService : IPdfService
    {
        private readonly IPDF _pdfClient;
        private readonly IAppResources _resourceService;
        private readonly IData _dataClient;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IProfile _profileClient;
        private readonly IRegister _registerClient;
        private readonly ICustomPdfHandler _customPdfHandler;
        private readonly string pdfElementType = "ref-data-as-pdf";
        private PDFContext _pdfContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="PdfService"/> class.
        /// </summary>
        /// <param name="pdfClient">Client for communicating with the Platform PDF service.</param>
        /// <param name="appResources">The service giving access to local resources.</param>
        /// <param name="dataClient">The data client.</param>
        /// <param name="httpContextAccessor">The httpContextAccessor</param>
        /// <param name="profileClient">The profile client</param>
        /// <param name="registerClient">The register client</param>
        /// <param name="customPdfHandler">Class for customizing pdf formatting and layout.</param>
        public PdfService(IPDF pdfClient, IAppResources appResources, IData dataClient, IHttpContextAccessor httpContextAccessor, IProfile profileClient, IRegister registerClient, ICustomPdfHandler customPdfHandler)
        {
            _pdfClient = pdfClient;
            _resourceService = appResources;
            _dataClient = dataClient;
            _httpContextAccessor = httpContextAccessor;
            _profileClient = profileClient;
            _registerClient = registerClient;
            _customPdfHandler = customPdfHandler;
        }

        /// <summary>
        /// Object containing all required data in order to produce the PDF.
        /// This is only exposed to show the values used to generate the PDF.
        /// The context is populated and exposed after the PDF is generated.
        /// Primary use is to allow for value checking as part of tests.
        /// </summary>
        public PDFContext GetPdfContext() 
        {
            return _pdfContext;
        }

        /// <inheritdoc/>
        public async Task GenerateAndStoreReceiptPDF(Instance instance, string taskId, DataElement dataElement, Type dataElementModelType)
        {
            string app = instance.AppId.Split("/")[1];
            string org = instance.Org;
            int instanceOwnerId = int.Parse(instance.InstanceOwner.PartyId);
            Guid instanceGuid = Guid.Parse(instance.Id.Split("/")[1]);

            string layoutSetsString = _resourceService.GetLayoutSets();
            LayoutSets layoutSets = null;
            LayoutSet layoutSet = null;
            if (!string.IsNullOrEmpty(layoutSetsString))
            {
                layoutSets = JsonConvert.DeserializeObject<LayoutSets>(layoutSetsString);
                layoutSet = layoutSets.Sets.FirstOrDefault(t => t.DataType.Equals(dataElement.DataType) && t.Tasks.Contains(taskId));
            }

            string layoutSettingsFileContent = layoutSet == null ? _resourceService.GetLayoutSettingsString() : _resourceService.GetLayoutSettingsStringForSet(layoutSet.Id);

            LayoutSettings layoutSettings = null;
            if (!string.IsNullOrEmpty(layoutSettingsFileContent))
            {
                layoutSettings = JsonConvert.DeserializeObject<LayoutSettings>(layoutSettingsFileContent);
            }

            // Ensure layoutsettings are initialized in FormatPdf
            layoutSettings ??= new();
            layoutSettings.Pages ??= new();
            layoutSettings.Pages.Order ??= new();
            layoutSettings.Pages.ExcludeFromPdf ??= new();
            layoutSettings.Components ??= new();
            layoutSettings.Components.ExcludeFromPdf ??= new();

            object data = await _dataClient.GetFormData(instanceGuid, dataElementModelType, org, app, instanceOwnerId, new Guid(dataElement.Id));

            layoutSettings = await _customPdfHandler.FormatPdf(layoutSettings, data);
            XmlSerializer serializer = new XmlSerializer(dataElementModelType);
            using MemoryStream stream = new MemoryStream();

            serializer.Serialize(stream, data);
            stream.Position = 0;

            byte[] dataAsBytes = new byte[stream.Length];
            await stream.ReadAsync(dataAsBytes);
            string encodedXml = Convert.ToBase64String(dataAsBytes);

            string language = "nb";
            Party actingParty = null;
            ClaimsPrincipal user = _httpContextAccessor.HttpContext.User;

            int? userId = user.GetUserIdAsInt();

            if (userId != null)
            {
                UserProfile userProfile = await _profileClient.GetUserProfile((int)userId);
                actingParty = userProfile.Party;

                if (!string.IsNullOrEmpty(userProfile.ProfileSettingPreference?.Language))
                {
                    language = userProfile.ProfileSettingPreference.Language;
                }
            }
            else
            {
                string orgNumber = user.GetOrgNumber().ToString();
                actingParty = await _registerClient.LookupParty(new PartyLookup { OrgNo = orgNumber });
            }

            // If layoutset exists pick correct layotFiles
            string formLayoutsFileContent = layoutSet == null ? _resourceService.GetLayouts() : _resourceService.GetLayoutsForSet(layoutSet.Id);

            TextResource textResource = await _resourceService.GetTexts(org, app, language);

            if (textResource == null && language != "nb")
            {
                // fallback to norwegian if texts does not exist
                textResource = await _resourceService.GetTexts(org, app, "nb");
            }

            string textResourcesString = JsonConvert.SerializeObject(textResource);
            Dictionary<string, Dictionary<string, string>> optionsDictionary =
                await GetOptionsDictionary(formLayoutsFileContent, language, data);

            var pdfContext = new PDFContext
            {
                Data = encodedXml,
                FormLayouts = JsonConvert.DeserializeObject<Dictionary<string, object>>(formLayoutsFileContent),
                LayoutSettings = layoutSettings,
                TextResources = JsonConvert.DeserializeObject(textResourcesString),
                OptionsDictionary = optionsDictionary,
                Party = await _registerClient.GetParty(instanceOwnerId),
                Instance = instance,
                UserParty = actingParty,
                Language = language
            };

            _pdfContext = pdfContext;

            Stream pdfContent = await _pdfClient.GeneratePDF(pdfContext);
            await StorePDF(pdfContent, instance, textResource);
            pdfContent.Dispose();
        }

        private async Task<DataElement> StorePDF(Stream pdfStream, Instance instance, TextResource textResource)
        {
            string fileName = null;
            string app = instance.AppId.Split("/")[1];

            TextResourceElement titleText =
                textResource.Resources.Find(textResourceElement => textResourceElement.Id.Equals("appName")) ??
                textResource.Resources.Find(textResourceElement => textResourceElement.Id.Equals("ServiceName"));

            if (titleText != null && !string.IsNullOrEmpty(titleText.Value))
            {
                fileName = titleText.Value + ".pdf";
            }
            else
            {
                fileName = app + ".pdf";
            }

            fileName = GetValidFileName(fileName);

            return await _dataClient.InsertBinaryData(
                instance.Id,
                pdfElementType,
                "application/pdf",
                fileName,
                pdfStream);
        }

        private static string GetValidFileName(string fileName)
        {
            fileName = Uri.EscapeDataString(fileName.AsFileName(false));
            return fileName;
        }

        private static List<string> GetOptionIdsFromFormLayout(string formLayout)
        {
            var optionsIds = new List<string>();
            string matchString = "\"optionsId\":\"";

            string[] formLayoutSubstrings = formLayout.Replace(" ", string.Empty).Split(new string[] { matchString }, StringSplitOptions.None);

            for (int i = 1; i < formLayoutSubstrings.Length; i++)
            {
                string[] workingSet = formLayoutSubstrings[i].Split('\"');
                string optionsId = workingSet[0];
                optionsIds.Add(optionsId);
            }

            return optionsIds;
        }

        private async Task<Dictionary<string, Dictionary<string, string>>> GetOptionsDictionary(string formLayout, string language, object data)
        {
            IEnumerable<JToken> componentsWithMappingDefined = GetFormComponentsWithMappingDefined(formLayout);
            Dictionary<string, object> componentMappingDefinitions = GetComponentMappingDefinitions(componentsWithMappingDefined);
            Dictionary<string, Dictionary<string, string>> componentKeyValuePairs = GetComponentKeyValuePairs(componentMappingDefinitions, data);

            Dictionary<string, Dictionary<string, string>> dictionary = new Dictionary<string, Dictionary<string, string>>();
            List<string> optionsIdsList = GetOptionIdsFromFormLayout(formLayout);

            foreach (string optionsId in optionsIdsList)
            {
                var hasMappings = componentKeyValuePairs.TryGetValue(optionsId, out Dictionary<string, string> optionsKeyValuePairs);
                AppOptions appOptions = await _resourceService.GetOptionsAsync(optionsId, language, hasMappings ? optionsKeyValuePairs : new Dictionary<string, string>());

                if (appOptions.Options != null && !dictionary.ContainsKey(optionsId))
                {
                    Dictionary<string, string> options = new Dictionary<string, string>();
                    foreach (AppOption item in appOptions.Options)
                    {
                        if (!options.ContainsKey(item.Label))
                        {
                            options.Add(item.Label, item.Value);
                        }
                    }

                    dictionary.Add(optionsId, options);
                }
            }

            return dictionary;
        }

        private static Dictionary<string, Dictionary<string, string>> GetComponentKeyValuePairs(Dictionary<string, object> componentMappingDefinitions, object data)
        {
            JObject jsonData = JObject.FromObject(data);
            var componentKeyValuePairs = new Dictionary<string, Dictionary<string, string>>();

            foreach (var mappingDef in componentMappingDefinitions)
            {
                var keyValuePairs = new Dictionary<string, string>();

                dynamic mappings = mappingDef.Value;
                foreach (var pair in mappings.Mappings)
                {
                    JToken selectedData = jsonData.SelectToken(pair.Key);
                    keyValuePairs.Add(pair.Value, selectedData.ToString());
                }

                componentKeyValuePairs.Add(mappings.OptionsId, keyValuePairs);
            }

            return componentKeyValuePairs;
        }

        private static Dictionary<string, object> GetComponentMappingDefinitions(IEnumerable<JToken> components)
        {
            var componentMappings = new Dictionary<string, object>();
            foreach (JToken component in components)
            {
                string componentId = component.SelectToken("id").ToString();
                string optionsId = component.SelectToken("optionsId").ToString();

                Dictionary<string, string> mappings = GetMappingsForComponent(component);

                componentMappings.Add(componentId, new { OptionsId = optionsId, Mappings = mappings });
            }

            return componentMappings;
        }

        private static Dictionary<string, string> GetMappingsForComponent(JToken component)
        {
            var maps = new Dictionary<string, string>();
            foreach (JProperty map in component.SelectToken("mapping").Children())
            {
                maps.Add(map.Name, map.Value.ToString());
            }

            return maps;
        }

        private static IEnumerable<JToken> GetFormComponentsWithMappingDefined(string formLayout)
        {
            JObject formLayoutObject = JObject.Parse(formLayout);

            // ? = Current object, ? = Filter, the rest is just dot notation ref. https://goessner.net/articles/JsonPath/
            return formLayoutObject.SelectTokens("FormLayout.data.layout[?(@.mapping)]");
        }
    }
}
