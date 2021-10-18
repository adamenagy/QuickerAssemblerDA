using Inventor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.Forge.DesignAutomation.Inventor.Utils;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;

namespace UpdateIPTParam
{
    [ComVisible(true)]
    public class SampleAutomation
    {
        private InventorServer m_server;
        public SampleAutomation(InventorServer app) { m_server = app; }

        public const string shelvesIamFile = "shelves.iam";
        public const string paramsJsonFile = "params.json";
        public const string outputPngFile = "output.png";
        public const string outputZipFile = "output.zip";
        public const string outputJsonFile = "output.json";

        public void Run(Document doc)
        {
            var task = RunAsync(doc);
            task.Wait(60000); // 1 minute
            if (task.Exception != null)
            {
                throw task.Exception;
            }
        }

        public async Task RunAsync(Document doc)
        {
            try
            {
                LogTrace("v3");

                string curDir = System.IO.Directory.GetCurrentDirectory();
                LogTrace("Current dir = " + curDir);

                string dllDdir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                LogTrace("Dll path = " + dllDdir);

                var docDir = System.IO.Path.Combine(dllDdir, "Shelving");

                var asm = m_server.Documents.Open(System.IO.Path.Combine(docDir, shelvesIamFile), false) as AssemblyDocument;
                LogTrace("Assembly path = " + asm.FullFileName);

                string paramsPath = System.IO.Path.Combine(curDir, paramsJsonFile);
                LogTrace("Params path = " + paramsPath);

                string data = System.IO.File.ReadAllText(paramsPath);
                LogTrace("After reading " + paramsJsonFile);

                JObject jParamsRoot = JObject.Parse(data);
                string text = jParamsRoot.ToString(Formatting.None);
                Trace.WriteLine(text);

                var directUpload = false;
                do {
                    directUpload = jParamsRoot["directUpload"].Value<bool>();
                    var outputPngUrl = jParamsRoot.ContainsKey("outputPngUrl") ? jParamsRoot["outputPngUrl"].Value<string>() : null;
                    var outputJsonUrl = jParamsRoot.ContainsKey("outputJsonUrl") ? jParamsRoot["outputJsonUrl"].Value<string>() : null;
                    var outputZipUrl = jParamsRoot.ContainsKey("outputZipUrl") ? jParamsRoot["outputZipUrl"].Value<string>() : null;

                    Transaction t = m_server.TransactionManager.StartTransaction(asm as _Document, "MyTransaction");

                    GenerateShelving(asm.ComponentDefinition, jParamsRoot["params"] as JObject);

                    if (outputPngUrl != null)
                    {
                        SavePicture(asm.ComponentDefinition, jParamsRoot["screenshot"] as JObject);
                        if (directUpload)
                        {
                            var outputPngCallback = jParamsRoot["outputPngCallback"].Value<string>();
                            await UploadFileAsync(outputPngUrl, outputPngFile);
                            _ = UploadDataAsync(outputPngCallback, "{ }");
                        }
                    }

                    if (outputJsonUrl != null)
                    {
                        string positionData = SavePositions(asm.ComponentDefinition);
                        if (directUpload)
                        {
                            _ = UploadDataAsync(outputJsonUrl, positionData);
                        }
                    }

                    if (outputZipUrl != null)
                    {
                        // We don't have the right to save files in the AppBundle's folder,
                        // so we'll save it to the working folder
                        LogTrace("Saving...");
                        var asmPath = System.IO.Path.Combine(curDir, shelvesIamFile);
                        asm.SaveAs(asmPath, true);

                        LogTrace("Zipping up files...");
                        string zipPath = System.IO.Path.Combine(curDir, outputZipFile);
                        ZipModelFiles(asm, asmPath, zipPath);

                        if (directUpload)
                        {
                            _ = UploadFileAsync(outputZipUrl, outputZipFile);
                        }
                    }

                    t.Abort();

                    if (directUpload)
                    {
                        jParamsRoot = await GetDataAsync(jParamsRoot["dataCallback"].Value<string>());
                    }
                } while (directUpload);
            }
            catch (Exception e) { LogTrace("RunAsync. Processing failed: {0}", e.ToString()); }
        }

        public async Task UploadFileAsync(string url, string fileName)
        {
            LogTrace("[UploadFile]");
            LogTrace(url + " / " + fileName);
            string curDir = System.IO.Directory.GetCurrentDirectory();
            string filePath = System.IO.Path.Combine(curDir, fileName);
            using (var client = new HttpClient())
            using (var fileStream = new StreamContent(System.IO.File.Open(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read)))
            {
                LogTrace("[UploadFile.PutAsync]");
                var response = await client.PutAsync(url, fileStream);
                LogTrace("[/UploadFile.PutAsync]");
                LogTrace("[/UploadFile]");
            }
        }

        public async Task UploadDataAsync(string url, string data)
        {
            try
            {
                LogTrace("[UploadData]");
                LogTrace(url);
                using (var client = new HttpClient())
                {

                    //LogTrace(jsonContent.Headers.ContentType.ToString());
                    //jsonContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    var content = new StringContent(data);
                    content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    //client.DefaultRequestHeaders.Add("Content-Type", "application/json");
                    var response = await client.PutAsync(url, content);// jsonContent);
                    LogTrace("[/UploadData]");
                }
            }
            catch (Exception e) { LogTrace("UploadData. Processing failed: {0}", e.ToString()); }
        }

        public async Task<JObject> GetDataAsync(string url)
        {
            LogTrace("[GetData]");
            LogTrace(url);
            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(url);
                LogTrace("[/GetData]");

                var data = await response.Content.ReadAsStringAsync();

                JObject j = JObject.Parse(data);

                return j;
            }
        }

        public void GenerateShelving(AssemblyComponentDefinition acd, JObject jParams)
        {
            Trace.WriteLine("GenerateShelving, jParams = " + jParams.ToString(Formatting.None));
            string height = jParams["height"].Value<string>();
            string shelfWidth = jParams["shelfWidth"].Value<string>();
            string numberOfColumns = jParams["numberOfColumns"].Value<string>();

            acd.Parameters["Height"].Expression = height;
            acd.Parameters["Columns"].Expression = numberOfColumns;
            acd.Parameters["ShelfWidth"].Expression = shelfWidth;
            // Kick off the model update
            acd.Parameters["iTrigger0"].Expression = $"{((double)acd.Parameters["iTrigger0"].Value + 1).ToString()}";
        }

        static IList<string> Split(string str, int chunkSize)
        {
            var list = new List<string>();
            var count = Convert.ToInt32(Math.Ceiling((double)str.Length / chunkSize));
            for (var i = 0; i < (count - 1); i++)
            {
                list.Add(str.Substring(i * chunkSize, chunkSize));
            }
            list.Add(str.Substring((count - 1) * chunkSize));

            return list;
        }

        public void SavePicture(AssemblyComponentDefinition acd, JObject jParams)
        {
            Trace.WriteLine("SavePicture, jParams = " + jParams.ToString(Formatting.None));
            var width = jParams["width"].Value<int>();
            var height = jParams["height"].Value<int>();

            TransientGeometry tg = m_server.TransientGeometry;
            Camera cam = m_server.TransientObjects.CreateCamera();
            cam.SceneObject = acd;

            cam.ViewOrientationType = ViewOrientationTypeEnum.kIsoTopRightViewOrientation;
            cam.Fit();
            cam.ApplyWithoutTransition();
            cam.SaveAsBitmap(outputPngFile, width, height, Type.Missing, Type.Missing);
        }

        public string SavePositions(AssemblyComponentDefinition acd)
        {
            Trace.WriteLine("SavePositions");
            JObject jRoot = new JObject();

            JArray jComponents = new JArray();
            foreach (ComponentOccurrence occ in acd.Occurrences)
            {
                PartDocument doc = occ.Definition.Document as PartDocument;
                string fileName = System.IO.Path.GetFileName(doc.FullFileName);
          
                JObject jComponent = new JObject();
                jComponent.Add("fileName", fileName);

                Matrix tr = occ.Transformation;
                double[] cells = new double[] { };
                tr.GetMatrixData(ref cells);

                JArray jCells = new JArray();
                foreach (double cell in cells)
                {
                    jCells.Add(cell);
                }
                jComponent.Add("cells", jCells);

                jComponents.Add(jComponent);
            }
            jRoot.Add("components", jComponents);

            string data = jRoot.ToString(Formatting.None);
            Trace.WriteLine($"data = {data}");

            System.IO.File.WriteAllText(outputJsonFile, data);

            return data;
        }

        public void ZipModelFiles(AssemblyDocument asm, string asmPath, string zipPath)
        {
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                string fileName = System.IO.Path.GetFileName(asmPath);
                archive.CreateEntryFromFile(asmPath, fileName);

                foreach (File f in asm.File.AllReferencedFiles)
                {
                    fileName = System.IO.Path.GetFileName(f.FullFileName);
                    archive.CreateEntryFromFile(f.FullFileName, fileName);
                }
            }
        }

        /// <summary>
        /// This will appear on the Design Automation output
        /// </summary>
        private static void LogTrace(string format, params object[] args) { Trace.TraceInformation(format, args); }
    }
}
