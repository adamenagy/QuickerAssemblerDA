/////////////////////////////////////////////////////////////////////
// Copyright (c) Autodesk, Inc. All rights reserved
// Written by Forge Partner Development
//
// Permission to use, copy, modify, and distribute this software in
// object code form for any purpose and without fee is hereby granted,
// provided that the above copyright notice appears in all copies and
// that both that copyright notice and the limited warranty and
// restricted rights notice below appear in all supporting
// documentation.
//
// AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
// AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
// MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
// DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
// UNINTERRUPTED OR ERROR FREE.
/////////////////////////////////////////////////////////////////////

using Autodesk.Forge;
using Autodesk.Forge.DesignAutomation;
using Autodesk.Forge.DesignAutomation.Model;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Activity = Autodesk.Forge.DesignAutomation.Model.Activity;
using Alias = Autodesk.Forge.DesignAutomation.Model.Alias;
using AppBundle = Autodesk.Forge.DesignAutomation.Model.AppBundle;
using Parameter = Autodesk.Forge.DesignAutomation.Model.Parameter;
using WorkItem = Autodesk.Forge.DesignAutomation.Model.WorkItem;
using WorkItemStatus = Autodesk.Forge.DesignAutomation.Model.WorkItemStatus;


namespace forgeSample.Controllers
{
    [ApiController]
    public class DesignAutomationController : ControllerBase
    {
        // Used to access the application folder (temp location for files & bundles)
        private IWebHostEnvironment _env;
        // used to access the SignalR Hub
        private IHubContext<DesignAutomationHub> _hubContext;
        // Local folder for bundles
        public string LocalBundlesFolder { get { return Path.Combine(_env.WebRootPath, "bundles"); } }
        public string LocalFilesFolder { get { return Path.Combine(_env.WebRootPath, "files"); } }
        /// Prefix for AppBundles and Activities
        public static string NickName { get { return OAuthController.GetAppSetting("FORGE_CLIENT_ID"); } }
        public static string BucketKey { get { return NickName.ToLower() + "-designautomation"; } }

        public static string QualifiedBundleActivityName { get { return string.Format("{0}.{1}+{2}", NickName, kBundleActivityName, Alias); } }
        /// Alias for the app (e.g. DEV, STG, PROD). This value may come from an environment variable
        public static string Alias { get { return "dev"; } }
        // Design Automation v3 API
        DesignAutomationClient _designAutomation;

        public const string kEngineName = "Autodesk.Inventor+24";
        public const string kBundleActivityName = "UpdateIPTParam";
        public const string kOutputFileName = "shelves.iam.zip";

        // Constructor, where env and hubContext are specified
        public DesignAutomationController(IWebHostEnvironment env, IHubContext<DesignAutomationHub> hubContext, DesignAutomationClient api)
        {
            _designAutomation = api;
            _env = env;
            _hubContext = hubContext;
        }

        /// <summary>
        /// Get all Activities defined for this account
        /// </summary>
        [HttpGet]
        [Route("api/forge/designautomation/activities")] 
        public async Task<List<string>> GetDefinedActivities()
        {
            System.Diagnostics.Debug.WriteLine("GetDefinedActivities");
            // filter list of 
            Page<string> activities = await _designAutomation.GetActivitiesAsync();
            List<string> definedActivities = new List<string>();
            foreach (string activity in activities.Data)
                if (activity.StartsWith(NickName) && activity.IndexOf("$LATEST") == -1)
                    definedActivities.Add(activity.Replace(NickName + ".", String.Empty));

            return definedActivities;
        }

        /// <summary>
        /// Helps identify the engine
        /// </summary>
        private string CommandLine()
        {
            return $"$(engine.path)\\InventorCoreConsole.exe /al $(appbundles[{kBundleActivityName}].path)";
        }

        /// <summary>
        /// Base64 enconde a string
        /// </summary>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// Define a new appbundle
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/files")]
        public async Task<IActionResult> UploadOssFiles([FromBody]JObject appBundleSpecs)
        {
            System.Diagnostics.Debug.WriteLine("UploadOssFiles");
            // OAuth token
            dynamic oauth = await OAuthController.GetInternalAsync();

            ObjectsApi objects = new ObjectsApi();
            objects.Configuration.AccessToken = oauth.access_token;

            DerivativesApi derivatives = new DerivativesApi();
            derivatives.Configuration.AccessToken = oauth.access_token;

            // upload file to OSS Bucket
            // 1. ensure bucket existis
            BucketsApi buckets = new BucketsApi();
            buckets.Configuration.AccessToken = oauth.access_token;
            try
            {
                PostBucketsPayload bucketPayload = new PostBucketsPayload(BucketKey, null, PostBucketsPayload.PolicyKeyEnum.Temporary);
                await buckets.CreateBucketAsync(bucketPayload, "US");
            }
            catch { }; // in case bucket already exists

            string [] filePaths = System.IO.Directory.GetFiles(LocalFilesFolder);
            foreach (string filePath in filePaths)
            {
                string fileName = System.IO.Path.GetFileName(filePath);
                using (StreamReader streamReader = new StreamReader(filePath))
                {
                    dynamic res = await objects.UploadObjectAsync(BucketKey, fileName, (int)streamReader.BaseStream.Length, streamReader.BaseStream, "application/octet-stream");

                    // prepare the payload
                    List<JobPayloadItem> outputs = new List<JobPayloadItem>()
                    {
                        new JobPayloadItem(
                            JobPayloadItem.TypeEnum.Svf,
                            new List<JobPayloadItem.ViewsEnum>()
                            {
                                JobPayloadItem.ViewsEnum._2d,
                                JobPayloadItem.ViewsEnum._3d
                            }
                        )
                    };
                    JobPayload job;
                    string urn = Base64Encode(res.objectId);
                    job = new JobPayload(new JobPayloadInput(urn), new JobPayloadOutput(outputs));

                    // start the translation
                    DerivativesApi derivative = new DerivativesApi();
                    derivative.Configuration.AccessToken = oauth.access_token;

                    derivative.TranslateAsync(job);
                }
            }
            

            return Ok();
        }

        /// <summary>
        /// Define a new appbundle
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/appbundles")]
        public async Task<IActionResult> CreateAppBundle([FromBody]JObject appBundleSpecs)
        {
            System.Diagnostics.Debug.WriteLine("CreateAppBundle");
            string zipFileName = "UpdateIPTParam.bundle";

            // check if ZIP with bundle is here
            string packageZipPath = Path.Combine(LocalBundlesFolder, zipFileName + ".zip");
            if (!System.IO.File.Exists(packageZipPath)) throw new Exception("Appbundle not found at " + packageZipPath);

            // get defined app bundles
            Page<string> appBundles = await _designAutomation.GetAppBundlesAsync();

            // check if app bundle is already define
            dynamic newAppVersion;
            string qualifiedAppBundleId = string.Format("{0}.{1}+{2}", NickName, kBundleActivityName, Alias);
            if (!appBundles.Data.Contains(qualifiedAppBundleId))
            {
                // create an appbundle (version 1)
                AppBundle appBundleSpec = new AppBundle()
                {
                    Package = kBundleActivityName,
                    Engine = kEngineName,
                    Id = kBundleActivityName,
                    Description = string.Format("Description for {0}", kBundleActivityName),

                };
                newAppVersion = await _designAutomation.CreateAppBundleAsync(appBundleSpec);
                if (newAppVersion == null) throw new Exception("Cannot create new app");

                // create alias pointing to v1
                Alias aliasSpec = new Alias() { Id = Alias, Version = 1 };
                Alias newAlias = await _designAutomation.CreateAppBundleAliasAsync(kBundleActivityName, aliasSpec);
            }
            else
            {
                // create new version
                AppBundle appBundleSpec = new AppBundle()
                {
                    Engine = kEngineName,
                    Description = kBundleActivityName
                };
                newAppVersion = await _designAutomation.CreateAppBundleVersionAsync(kBundleActivityName, appBundleSpec);
                if (newAppVersion == null) throw new Exception("Cannot create new version");

                // update alias pointing to v+1
                AliasPatch aliasSpec = new AliasPatch()
                {
                    Version = newAppVersion.Version
                };
                Alias newAlias = await _designAutomation.ModifyAppBundleAliasAsync(kBundleActivityName, Alias, aliasSpec);
            }

            // upload the zip with .bundle
            RestClient uploadClient = new RestClient(newAppVersion.UploadParameters.EndpointURL);
            RestRequest request = new RestRequest(string.Empty, Method.POST);
            request.AlwaysMultipartFormData = true;
            foreach (KeyValuePair<string, string> x in newAppVersion.UploadParameters.FormData) request.AddParameter(x.Key, x.Value);
            request.AddFile("file", packageZipPath);
            request.AddHeader("Cache-Control", "no-cache");
            await uploadClient.ExecuteTaskAsync(request);

            return Ok(new { AppBundle = QualifiedBundleActivityName, Version = newAppVersion.Version });
        }

        /// <summary>
        /// Define a new activity
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/activities")]
        public async Task<IActionResult> CreateActivity([FromBody]JObject activitySpecs)
        {
            System.Diagnostics.Debug.WriteLine("CreateActivity");
            Page<string> activities = await _designAutomation.GetActivitiesAsync();
            if (!activities.Data.Contains(QualifiedBundleActivityName))
            {
                string commandLine = CommandLine();
                Activity activitySpec = new Activity()
                {
                    Id = kBundleActivityName,
                    Appbundles = new List<string>() { QualifiedBundleActivityName },
                    CommandLine = new List<string>() { commandLine },
                    Engine = kEngineName,
                    Parameters = new Dictionary<string, Parameter>()
                    {
                        { "inputJson", new Parameter() { Description = "input json", LocalName = "params.json", Ondemand = false, Required = true, Verb = Verb.Get, Zip = false } },
                        { "outputZip", new Parameter() { Description = "output zip file", LocalName = "output.zip", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } },
                        { "outputPng", new Parameter() { Description = "output png file", LocalName = "output.png", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } },
                        { "outputJson", new Parameter() { Description = "output json file", LocalName = "output.json", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } }
                    }
                };
                Activity newActivity = await _designAutomation.CreateActivityAsync(activitySpec);

                // specify the alias for this Activity
                Alias aliasSpec = new Alias() { Id = Alias, Version = 1 };
                Alias newAlias = await _designAutomation.CreateActivityAliasAsync(kBundleActivityName, aliasSpec);

                return Ok(new { Activity = QualifiedBundleActivityName });
            }

            // as this activity points to a AppBundle "dev" alias (which points to the last version of the bundle),
            // there is no need to update it (for this sample), but this may be extended for different contexts
            return Ok(new { Activity = "Activity already defined" });
        }

        /// <summary>
        /// Start a new workitem
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/workitems")]
        public async Task<IActionResult> StartWorkitems([FromBody]JObject input)
        {
            System.Diagnostics.Debug.WriteLine("StartWorkitem");
            string browerConnectionId = input["browerConnectionId"].Value<string>();

            // OAuth token
            dynamic oauth = await OAuthController.GetInternalAsync();

            string pngFileName = browerConnectionId + ".png";                
            string pngWorkItemId = await CreateWorkItem(
                input,
                new Dictionary<string, string>() { { "Authorization", "Bearer " + oauth.access_token } },
                browerConnectionId,
                "outputPng",
                pngFileName,
                string.Format("https://developer.api.autodesk.com/oss/v2/buckets/{0}/objects/{1}", BucketKey, pngFileName)
            );

            string jsonFileName = browerConnectionId + ".json";
            string jsonWorkItemId = await CreateWorkItem(
                input,
                new Dictionary<string, string>() { { "Content-Type", "application/json" } },
                browerConnectionId,
                "outputJson",
                jsonFileName,
                string.Format("{0}/api/forge/callback/ondata/json?id={1}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browerConnectionId)
            );

            /*
            string zipWorkItemId = await CreateWorkItem(
                input,
                oauth.access_token,
                browerConnectionId,
                "outputZip",
                string.Format("https://developer.api.autodesk.com/oss/v2/buckets/{0}/objects/{1}", BucketKey, kOutputFileName)
            );
            */

            return Ok(new {
                PngWorkItemId = "pngWorkItemId",
                JsonWorkItemId = "jsonWorkItemId",
                ZipWorkItemId = "zipWorkItemId"
            });
        }
        private async Task<string> CreateWorkItem(JObject input, Dictionary<string, string> headers, string browerConnectionId, string outputName, string fileName, string url)
        {
            input["output"] = outputName;
            XrefTreeArgument inputJsonArgument = new XrefTreeArgument()
            {
                Url = "data:application/json," + input.ToString(Formatting.None)
            };

            XrefTreeArgument outputArgument = new XrefTreeArgument()
            {
                Url = url,
                Verb = Verb.Put,
                Headers = headers
            };

            string callbackComplete = string.Format(
                "{0}/api/forge/callback/oncomplete?id={1}&outputFile={2}", 
                OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), 
                browerConnectionId, 
                fileName);

            WorkItem workItemSpec = new WorkItem()
            {
                ActivityId = QualifiedBundleActivityName,
                Arguments = new Dictionary<string, IArgument>()
                {
                    { "inputJson", inputJsonArgument },
                    { outputName, outputArgument },
                    //{ "onProgress", new XrefTreeArgument { Verb = Verb.Post, Url = callbackProgress } },
                    { "onComplete", new XrefTreeArgument { Verb = Verb.Post, Url = callbackComplete } }
                }
            };
            WorkItemStatus workItemStatus = await _designAutomation.CreateWorkItemAsync(workItemSpec);

            return workItemStatus.Id;
        }

        /// <summary>
        /// Define a new appbundle
        /// test with curl:
        /// with form: curl -F 'img_avatar=@/Users/nagyad/Documents/boxHammer.csv' http://localhost:3000/api/forge/callback/ondata/png
        /// file: curl -X POST --header "Content-Type:application/octet-stream" --data @/Users/nagyad/Documents/boxHammer.csv http://localhost:3000/api/forge/callback/ondata/png
        /// json: curl -X POST --header "Content-Type:application/json" --data '{"hello":"value"}' http://localhost:3000/api/forge/callback/ondata/json
        /// </summary>
        [HttpPut]
        [Route("api/forge/callback/ondata/json")]
        public async Task<IActionResult> OnData([FromRoute] string dataType, [FromQuery] string id, [FromBody] JObject data)
        {
            System.Diagnostics.Debug.WriteLine("OnData, dataType = " + dataType);

            await _hubContext.Clients.Client(id).SendAsync("onComponents", data.ToString(Formatting.None));
            
            return Ok();
        }

        /// <summary>
        /// Define a new appbundle
        /// basic: curl -X POST --header "Content-Type:application/json" --data '{"hello":"value"}' http://localhost:3000/api/forge/callback/onprogress
        /// </summary>
        [HttpPost]
        [Route("api/forge/callback/onprogress")]
        public async Task<IActionResult> OnProgress(string id, [FromBody]JObject data)
        {
            System.Diagnostics.Debug.WriteLine("OnProgress, data = " + data.ToString());

            if (!data.ContainsKey("progress"))
                return Ok();

            try
            {
                string base64 = data["progress"].Value<string>();

                if (base64.StartsWith("png"))
                    await _hubContext.Clients.Client(id).SendAsync("onPicture", base64);
                else
                    await _hubContext.Clients.Client(id).SendAsync("onComponents", base64);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine("OnProgress, e.Message = " + e.Message);
            }

            return Ok();
        }

        /// <summary>
        /// Callback from Design Automation Workitem (onProgress or onComplete)
        /// </summary>
        [HttpPost]
        [Route("/api/forge/callback/oncomplete")]
        public async Task<IActionResult> OnComplete(string id, string outputFile, [FromBody]dynamic body)
        {
            System.Diagnostics.Debug.WriteLine($"OnComplete, id = {id}, outputFile = {outputFile}");
            try
            {
                // your webhook should return immediately! we can use Hangfire to schedule a job
                JObject bodyJson = JObject.Parse((string)body.ToString());
                await _hubContext.Clients.Client(id).SendAsync("onComplete", bodyJson.ToString());

                var client = new RestClient(bodyJson["reportUrl"].Value<string>());
                var request = new RestRequest(string.Empty);

                byte[] bs = client.DownloadData(request);
                string report = System.Text.Encoding.Default.GetString(bs);
                await _hubContext.Clients.Client(id).SendAsync("onComplete", report);

                if (outputFile.EndsWith(".png"))
                {
                    dynamic oauth = await OAuthController.GetInternalAsync();
                    ObjectsApi objects = new ObjectsApi();
                    objects.Configuration.AccessToken = oauth.access_token;
                    dynamic signedUrl = await objects.CreateSignedResourceAsyncWithHttpInfo(BucketKey, outputFile, new PostBucketsSigned(10), "read");
                    await _hubContext.Clients.Client(id).SendAsync("onPicture", (string)(signedUrl.Data.signedUrl));
                } 
            }
            catch (Exception e) 
            {
                System.Diagnostics.Debug.WriteLine("OnComplete, e.Message = " + e.Message);
            }

            // ALWAYS return ok (200)
            return Ok();
        }

        /// <summary>
        /// Return a list of available engines
        /// </summary>
        [HttpGet]
        [Route("api/forge/designautomation/engines")]
        public async Task<List<string>> GetAvailableEngines()
        {
            dynamic oauth = await OAuthController.GetInternalAsync();

            // define Engines API
            Page<string> engines = await _designAutomation.GetEnginesAsync();
            engines.Data.Sort();

            return engines.Data; // return list of engines
        }

        /// <summary>
        /// Clear the accounts (for debugging purposes)
        /// </summary>
        [HttpDelete]
        [Route("api/forge/designautomation/account")]
        public async Task<IActionResult> ClearAccount()
        {
            // clear account
            await _designAutomation.DeleteForgeAppAsync("me");
            return Ok();
        }

        /// <summary>
        /// Names of app bundles on this project
        /// </summary>
        [HttpGet]
        [Route("api/appbundles")]
        public string[] GetLocalBundles()
        {
            // this folder is placed under the public folder, which may expose the bundles
            // but it was defined this way so it be published on most hosts easily
            return Directory.GetFiles(LocalBundlesFolder, "*.zip").Select(Path.GetFileNameWithoutExtension).ToArray();
        }
    }

    /// <summary>
    /// Class uses for SignalR
    /// </summary>
    public class DesignAutomationHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public string GetConnectionId() 
        {
            System.Diagnostics.Debug.WriteLine("GetConnectionId, Context.ConnectionId = " + Context.ConnectionId);
            return Context.ConnectionId; 
        }
    }

}