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
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;

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
        public static string NickName { get { return OAuthController.GetAppSetting("FORGE_NICKNAME"); } }
        public static string BucketKey { get { return NickName.ToLower() + "-designautomation"; } }

        public static string QualifiedBundleActivityName { get { return string.Format("{0}.{1}+{2}", NickName, kBundleActivityName, Alias); } }
        /// Alias for the app (e.g. DEV, STG, PROD). This value may come from an environment variable
        public static string Alias { get { return "dev"; } }
        // Design Automation v3 API
        DesignAutomationClient _designAutomation;

        public static Dictionary<string, string> _runningWorkitems = new Dictionary<string, string>();
        public static Dictionary<string, TaskCompletionSource<JObject>> _runningWorkitemTasks = new Dictionary<string, TaskCompletionSource<JObject>>();

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
            return $"$(engine.path)\\InventorCoreConsole.exe /al \"$(appbundles[{kBundleActivityName}].path)\"";
        }

        /// <summary>
        /// Base64 enconde a string
        /// </summary>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        public static string MD5Encode(JObject obj)
        {
            using MD5 md5 = MD5.Create();

            md5.Initialize();
            md5.ComputeHash(Encoding.UTF8.GetBytes(obj.ToString(Formatting.None)));
            return BitConverter.ToString(md5.Hash).Replace("-", "");
        } 

        /// <summary>
        /// Upload sample files
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/files")]
        public async Task<IActionResult> UploadOssFiles([FromBody]JObject appBundleSpecs)
        {
            if (OAuthController.GetAppSetting("DISABLE_SETUP") == "true")
            {
                return Unauthorized();
            }

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
                PostBucketsPayload bucketPayload = new PostBucketsPayload(BucketKey, null, PostBucketsPayload.PolicyKeyEnum.Transient);
                await buckets.CreateBucketAsync(bucketPayload, "US");
            }
            catch { }; // in case bucket already exists

            string [] filePaths = System.IO.Directory.GetFiles(LocalFilesFolder);
            foreach (string filePath in filePaths)
            {
                string fileName = System.IO.Path.GetFileName(filePath);
                using StreamReader streamReader = new StreamReader(filePath);

                dynamic res = await objects.UploadObjectAsync(BucketKey, fileName, (int)streamReader.BaseStream.Length, streamReader.BaseStream, "application/octet-stream");

                _ = TranslateFileAsync(res.objectId, null);
            }    

            return Ok();
        }

        private async Task TranslateFileAsync(string objectId, string rootFileName)
        {
            dynamic oauth = await OAuthController.GetInternalAsync();

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
            string urn = Base64Encode(objectId);
            if (rootFileName != null)
            {
                job = new JobPayload(new JobPayloadInput(urn, true, rootFileName), new JobPayloadOutput(outputs));
            }
            else
            {
                job = new JobPayload(new JobPayloadInput(urn), new JobPayloadOutput(outputs));
            }

            // start the translation
            DerivativesApi derivative = new DerivativesApi();
            derivative.Configuration.AccessToken = oauth.access_token;

            await derivative.TranslateAsync(job);
        }

        /// <summary>
        /// Get files in bucket
        /// </summary>
        [HttpGet]
        [Route("api/forge/designautomation/files")]
        public async Task<IActionResult> GetOssFiles()
        {
            System.Diagnostics.Debug.WriteLine("GetOssFiles");
            // OAuth token
            dynamic oauth = await OAuthController.GetInternalAsync();

            ObjectsApi objects = new ObjectsApi();
            objects.Configuration.AccessToken = oauth.access_token;

            dynamic res = await objects.GetObjectsAsync(BucketKey);
            
            return Ok(res);
        }

        /// <summary>
        /// Define a new appbundle
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/appbundles")]
        public async Task<IActionResult> CreateAppBundle([FromBody]JObject appBundleSpecs)
        {
            if (OAuthController.GetAppSetting("DISABLE_SETUP") == "true")
            {
                return Unauthorized();
            }

            System.Diagnostics.Debug.WriteLine("CreateAppBundle");
            string zipFileName = "UpdateIPTParam.bundle";

            // check if ZIP with bundle is here
            string packageZipPath = Path.Combine(LocalBundlesFolder, zipFileName + ".zip");
            if (!System.IO.File.Exists(packageZipPath)) throw new Exception("Appbundle not found at " + packageZipPath);

            // get defined app bundles
            List<string> allAppBbundles = new List<string>();
            string paginationToken = null;
            while (true)
            {
                Page<string> appBundles = await _designAutomation.GetAppBundlesAsync(paginationToken);
                allAppBbundles.AddRange(appBundles.Data);
                if (appBundles.PaginationToken == null)
                    break;
                paginationToken = appBundles.PaginationToken;
            }

            // check if app bundle is already define
            dynamic newAppVersion;
            string qualifiedAppBundleId = string.Format("{0}.{1}+{2}", NickName, kBundleActivityName, Alias);
            if (!allAppBbundles.Contains(qualifiedAppBundleId))
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
            using (var client = new HttpClient())
            {
                using (var formData = new MultipartFormDataContent())
                {
                    foreach (var kv in newAppVersion.UploadParameters.FormData)
                    {
                        if (kv.Value != null)
                        {
                            formData.Add(new StringContent(kv.Value), kv.Key);
                        }
                    }
                    using (var content = new StreamContent(new FileStream(packageZipPath, FileMode.Open)))
                    {
                        formData.Add(content, "file");
                        using (var request = new HttpRequestMessage(HttpMethod.Post, newAppVersion.UploadParameters.EndpointURL) { Content = formData })
                        {
                            var response = await client.SendAsync(request);
                            response.EnsureSuccessStatusCode();
                        }
                    }
                }
            }

            return Ok(new { AppBundle = QualifiedBundleActivityName, Version = newAppVersion.Version });
        }

        /// <summary>
        /// Define a new activity
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/activities")]
        public async Task<IActionResult> CreateActivity([FromBody]JObject activitySpecs)
        {
            if (OAuthController.GetAppSetting("DISABLE_SETUP") == "true")
            {
                return Unauthorized();
            }

            System.Diagnostics.Debug.WriteLine("CreateActivity");


            List<string> allActivities = new List<string>();
            string paginationToken = null;
            while (true)
            { 
                Page<string> activities = await _designAutomation.GetActivitiesAsync(paginationToken);
                allActivities.AddRange(activities.Data);
                if (activities.PaginationToken == null)
                    break;
                paginationToken = activities.PaginationToken;
            }

            if (!allActivities.Contains(QualifiedBundleActivityName))
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
                        { "outputZipUrl", new Parameter() { Description = "output zip file", LocalName = "output.zip", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } },
                        { "outputPngUrl", new Parameter() { Description = "output png file", LocalName = "output.png", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } },
                        { "outputJsonUrl", new Parameter() { Description = "output json file", LocalName = "output.json", Ondemand = false, Required = false, Verb = Verb.Put, Zip = false } }
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
        /// Define a new activity
        /// </summary>
        public static async Task<bool> IsInCacheAsync(string fileName)
        {
            dynamic oauth = await OAuthController.GetInternalAsync();
            ObjectsApi objects = new ObjectsApi();
            objects.Configuration.AccessToken = oauth.access_token;

            try
            {
                dynamic res = await objects.GetObjectDetailsAsync (BucketKey, fileName);
                return true;
            } catch {}
            
            return false;
        } 

        /// <summary>
        /// Start a new workitem
        /// </summary>
        [HttpPost]
        [Route("api/forge/designautomation/workitems")]
        public async Task<IActionResult> StartWorkitems([FromBody]JObject input)
        {
            System.Diagnostics.Debug.WriteLine("StartWorkitem");
            string browserConnectionId = input["browserConnectionId"].Value<string>();
            bool useCache = input["useCache"].Value<bool>();
            bool keepWorkitem = input["keepWorkitem"].Value<bool>();
            string pngWorkItemId = "skipped";
            string jsonWorkItemId = "skipped";
            string zipWorkItemId = "skipped";
            string sessionWorkItemId = "skipped";

            // OAuth token
            dynamic oauth = await OAuthController.GetInternalAsync();

            string hash = MD5Encode(input["params"] as JObject);
            string zipFileName = hash + ".zip";
            string pngFileName = hash + ".png";
            string jsonFileName = browserConnectionId + ".json";

            if (useCache && await IsInCacheAsync(zipFileName))
            {
                double[] cells = new double[] {
                    1, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0,
                    0, 0, 0, 1
                };

                _ = SendPictureUrlToClientAsync(browserConnectionId, pngFileName);

                JObject data = new JObject(
                    new JProperty("components",
                        new JArray(
                            new JObject(
                                new JProperty("fileName", zipFileName),
                                new JProperty("cells", cells)
                            )
                        )
                    )
                );
                _ = SendComponentsDataToClientAsync(browserConnectionId, data);

                return Ok(new {
                    PngWorkItemId = pngWorkItemId,
                    JsonWorkItemId = jsonWorkItemId,
                    ZipWorkItemId = zipWorkItemId
                });
            }

            if (keepWorkitem)
            {
                sessionWorkItemId = await CreateSessionWorkItemAsync(
                    input,
                    new Dictionary<string, string>() { { "Authorization", "Bearer " + oauth.access_token } },
                    browserConnectionId,
                    jsonFileName,
                    pngFileName,
                    useCache ? zipFileName : null
                );

                return Ok(new
                {
                    PngWorkItemId = pngWorkItemId,
                    JsonWorkItemId = jsonWorkItemId,
                    ZipWorkItemId = zipWorkItemId,
                    SessionWorkItemId = sessionWorkItemId
                });
            }

            pngWorkItemId = await CreateWorkItemAsync(
                input,
                new Dictionary<string, string>() { { "Authorization", "Bearer " + oauth.access_token } },
                browserConnectionId,
                "outputPngUrl",
                pngFileName,
                string.Format("https://developer.api.autodesk.com/oss/v2/buckets/{0}/objects/{1}", BucketKey, pngFileName)
            );

            if (useCache)
            {
                zipWorkItemId = await CreateWorkItemAsync(
                    input,
                    new Dictionary<string, string>() { { "Authorization", "Bearer " + oauth.access_token } },
                    browserConnectionId,
                    "outputZipUrl",
                    zipFileName,
                    string.Format("https://developer.api.autodesk.com/oss/v2/buckets/{0}/objects/{1}", BucketKey, zipFileName)
                );
            }

            jsonWorkItemId = await CreateWorkItemAsync(
                input,
                new Dictionary<string, string>() { { "Content-Type", "application/json" } },
                browserConnectionId,
                "outputJsonUrl",
                jsonFileName,
                string.Format("{0}/api/forge/callback/ondata/json?id={1}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browserConnectionId)
            );

            return Ok(new {
                PngWorkItemId = pngWorkItemId,
                JsonWorkItemId = jsonWorkItemId,
                ZipWorkItemId = zipWorkItemId,
                SessionWorkItemId = sessionWorkItemId
            });
        }
        private async Task<string> CreateWorkItemAsync(JObject input, Dictionary<string, string> headers, string browserConnectionId, string outputName, string fileName, string url)
        {
            input["directUpload"] = false;
            input[outputName] = url;
            XrefTreeArgument inputJsonArgument = new XrefTreeArgument()
            {
                Url = "data:application/json," + input.ToString(Formatting.None)
            };
            input.Remove(outputName);

            XrefTreeArgument outputArgument = new XrefTreeArgument()
            {
                Url = url,
                Verb = Verb.Put,
                Headers = headers
            };

            string callbackComplete = string.Format(
                "{0}/api/forge/callback/oncomplete?id={1}&outputFile={2}", 
                OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), 
                browserConnectionId, 
                fileName);

            WorkItem workItemSpec = new WorkItem()
            {
                ActivityId = QualifiedBundleActivityName,
                Arguments = new Dictionary<string, IArgument>()
                {
                    { "inputJson", inputJsonArgument },
                    { outputName, outputArgument },
                    { "onComplete", new XrefTreeArgument { Verb = Verb.Post, Url = callbackComplete } }
                }
            };
            WorkItemStatus workItemStatus = await _designAutomation.CreateWorkItemAsync(workItemSpec);

            return workItemStatus.Id;
        }

        private async Task<string> CreateSessionWorkItemAsync(JObject input, Dictionary<string, string> headers, string browserConnectionId, string jsonFileName, string pngFileName, string zipFileName)
        {
            if (zipFileName != null)
            {
                input["outputZipUrl"] = zipFileName;
            }

            input["directUpload"] = true;
            input["outputJsonUrl"] = string.Format("{0}/api/forge/callback/ondata/json?id={1}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browserConnectionId);
            
            //input["outputPngUrl"] = pngFileName;
            string signedUrl = await CreateSignedResourceAsync(pngFileName, "write");
            input["outputPngUrl"] = signedUrl;

            //input["outputPngUrl"] = string.Format("{0}/api/forge/callback/ondata/png?id={1}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browserConnectionId);
            input["outputPngCallback"] = string.Format("{0}/api/forge/callback/ondata/png?id={1}&outputFile={2}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browserConnectionId, pngFileName);

            input["dataCallback"] = string.Format("{0}/api/forge/designautomation/data?id={1}", OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"), browserConnectionId);

            if (_runningWorkitems.ContainsKey(browserConnectionId))
            {
                _runningWorkitemTasks[browserConnectionId].SetResult(input);

                return _runningWorkitems[browserConnectionId];
            }

            XrefTreeArgument inputJsonArgument = new XrefTreeArgument()
            {
                Url = "data:application/json," + input.ToString(Formatting.None)
            };

            string callbackComplete = string.Format(
                "{0}/api/forge/callback/oncomplete?id={1}&outputFile={2}",
                OAuthController.GetAppSetting("FORGE_WEBHOOK_URL"),
                browserConnectionId,
                jsonFileName);

            WorkItem workItemSpec = new WorkItem()
            {
                ActivityId = QualifiedBundleActivityName,
                Arguments = new Dictionary<string, IArgument>()
                {
                    { "inputJson", inputJsonArgument },
                    { "onComplete", new XrefTreeArgument { Verb = Verb.Post, Url = callbackComplete } }
                }
            };
            WorkItemStatus workItemStatus = await _designAutomation.CreateWorkItemAsync(workItemSpec);

            _runningWorkitems[browserConnectionId] = workItemStatus.Id;

            return workItemStatus.Id;
        }

        private async Task SendComponentsDataToClientAsync(string id, JObject data)
        {
            data["urnBase"] = "urn:adsk.objects:os.object:" + BucketKey + "/";
            await _hubContext.Clients.Client(id).SendAsync("onComponents", data.ToString(Formatting.None));
        }

        private async Task SendStringToClientAsync(string id, string data)
        {
            await _hubContext.Clients.Client(id).SendAsync("onComplete", data);
        }

        private async Task<string> CreateSignedResourceAsync(string fileName, string accessType)
        {
            System.Diagnostics.Debug.WriteLine("CreateSignedResourceAsync: " + fileName);

            dynamic oauth = await OAuthController.GetInternalAsync();
            ObjectsApi objects = new ObjectsApi();
            objects.Configuration.AccessToken = oauth.access_token;

            dynamic details;
            try
            {
                details = await objects.GetObjectDetailsAsyncWithHttpInfo(BucketKey, fileName);
            }
            catch (Autodesk.Forge.Client.ApiException ex)
            {
                if (ex.ErrorCode == 404)
                {
                    await objects.UploadObjectAsyncWithHttpInfo(BucketKey, fileName, 0, new MemoryStream(0));
                }
            }
            
            // if using "readwrite" then it should succeed even if the file does not exist yet
            dynamic signedUrl = await objects.CreateSignedResourceAsyncWithHttpInfo(BucketKey, fileName, new PostBucketsSigned(10), accessType);

            return signedUrl.Data.signedUrl;
        }

        private async Task SendPictureUrlToClientAsync(string id, string pngFile)
        {
            System.Diagnostics.Debug.WriteLine("SendPictureUrlToClient: " + pngFile);

            string signedUrl = await CreateSignedResourceAsync(pngFile, "read");
            await _hubContext.Clients.Client(id).SendAsync("onPicture", signedUrl);
        }

        /// <summary>
        /// Define a new appbundle
        /// test with curl:
        /// with form: curl -F 'img_avatar=@/Users/nagyad/Documents/boxHammer.csv' http://localhost:3000/api/forge/callback/ondata/png
        /// file: curl -X POST --header "Content-Type:application/octet-stream" --data @/Users/nagyad/Documents/boxHammer.csv http://localhost:3000/api/forge/callback/ondata/png
        /// json: curl -X POST --header "Content-Type:application/json" --data '{"hello":"value"}' http://localhost:3000/api/forge/callback/ondata/json
        /// </summary>
        [HttpPut]
        [Route("api/forge/callback/ondata/{fileType}")]
        public async Task<IActionResult> OnData([FromRoute] string fileType, [FromQuery] string id, [FromQuery] string outputFile, [FromBody] dynamic data)
        {
            System.Diagnostics.Debug.WriteLine("OnData: " + outputFile);

            // urnBase, something like "urn:adsk.objects:os.object:rgm0mo9jvssd2ybedk9mrtxqtwsa61y0-designautomation/"
            if (fileType == "json")
                _ = SendComponentsDataToClientAsync(id, data);

            if (fileType == "png")
                _ = SendPictureUrlToClientAsync(id, outputFile);
                
            return Ok();
        }

        /// <summary>
        /// Called by the code in the app bundle to get the latest data 
        /// to use for the model configuration
        /// </summary>
        [HttpGet]
        [Route("api/forge/designautomation/data")]
        public async Task<IActionResult> GetDataAsync([FromQuery] string id)
        {
            System.Diagnostics.Debug.WriteLine("GetData");

            _runningWorkitemTasks[id] = new TaskCompletionSource<JObject>();
            JObject data = await _runningWorkitemTasks[id].Task;

            return Ok(data.ToString());
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
                _ = SendStringToClientAsync(id, bodyJson.ToString());

                if (_runningWorkitems.ContainsKey(id) && _runningWorkitems[id] == bodyJson["id"].Value<string>())
                {
                    _runningWorkitems.Remove(id);
                }

                var client = new RestClient(bodyJson["reportUrl"].Value<string>());
                var request = new RestRequest(string.Empty);

                byte[] bs = client.DownloadData(request);
                string report = System.Text.Encoding.Default.GetString(bs);
                _ = SendStringToClientAsync(id, report);

                if (outputFile.EndsWith(".png"))
                {
                    _ = SendPictureUrlToClientAsync(id, outputFile);
                } 

                if (outputFile.EndsWith(".zip"))
                {
                    string objectId = "urn:adsk.objects:os.object:" + BucketKey + "/" + outputFile;
                    _ = TranslateFileAsync(objectId, "shelves.iam");
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
        /// Clear the accounts (for debugging purposes)
        /// </summary>
        [HttpDelete]
        [Route("api/forge/designautomation/account")]
        public async Task<IActionResult> ClearAccount()
        {
           if (OAuthController.GetAppSetting("DISABLE_SETUP") == "true")
           {
               return Unauthorized();
           }

            // clear account
            await _designAutomation.DeleteForgeAppAsync("me");
            return Ok();
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