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

namespace UpdateIPTParam
{
    [ComVisible(true)]
    public class SampleAutomation
    {
        private InventorServer m_server;
        public SampleAutomation(InventorServer app) { m_server = app; }

        public void Run(Document doc)
        {
            try
            {
                string curDir = System.IO.Directory.GetCurrentDirectory();
                LogTrace("Current dir = " + curDir);

                string dllDdir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                LogTrace("Dll path = " + dllDdir);

                var docDir = System.IO.Path.Combine(dllDdir, "Shelving");

                var asm = m_server.Documents.Open(System.IO.Path.Combine(docDir, "shelves.iam"), false) as AssemblyDocument;
                LogTrace("Assembly path = " + asm.FullFileName);

                string paramsPath = System.IO.Path.Combine(curDir, "params.json");
                LogTrace("Params path = " + paramsPath);

                string data = System.IO.File.ReadAllText(paramsPath);
                LogTrace("After reading params.json");
                //this errors out :-s >> LogTrace($"Params content = {data}");

                JObject jParamsRoot = JObject.Parse(data);
                string text = jParamsRoot.ToString(Formatting.None);
                Trace.Write(text);

                GenerateShelving(asm.ComponentDefinition, jParamsRoot["params"] as JObject);

                //LogTrace("Updating...");
                //asm.Update2(true);

                var output = jParamsRoot["output"].Value<string>();
                switch (output)
                {
                    case "outputPng":
                        SendPicture(asm.ComponentDefinition, jParamsRoot["screenshot"] as JObject);
                        break;

                    case "outputJson":
                        SendPositions(asm.ComponentDefinition);
                        break;

                    default:
                        // We don't have the right to save files in the AppBundle's folder,
                        // so we'll save it to the working folder
                        LogTrace("Saving...");
                        var asmPath = System.IO.Path.Combine(curDir, "shelves.iam");
                        asm.SaveAs(asmPath, true);

                        LogTrace("Zipping up files...");
                        string zipPath = System.IO.Path.Combine(curDir, "output.zip");
                        ZipModelFiles(asm, asmPath, zipPath);
                        break;
                }  
            }
            catch (Exception e) { LogTrace("Processing failed: {0}", e.ToString()); }
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
            acd.Parameters["iTrigger0"].Expression = $"{(acd.Parameters["iTrigger0"].Value + 1).ToString()}";
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

        public void SendPicture(AssemblyComponentDefinition acd, JObject jParams)
        {
            Trace.WriteLine("SendPicture, jParams = " + jParams.ToString(Formatting.None));
            var width = jParams["width"].Value<int>();
            var height = jParams["height"].Value<int>();

            TransientGeometry tg = m_server.TransientGeometry;
            Camera cam = m_server.TransientObjects.CreateCamera();
            cam.SceneObject = acd;

            cam.ViewOrientationType = ViewOrientationTypeEnum.kIsoTopRightViewOrientation;
            cam.Fit();
            cam.ApplyWithoutTransition();
            cam.SaveAsBitmap("output.png", width, height, Type.Missing, Type.Missing);
        }

        public void SendPositions(AssemblyComponentDefinition acd)
        {
            Trace.WriteLine("SendPositions");
            JObject jRoot = new JObject();

            JArray jComponents = new JArray();
            foreach (ComponentOccurrence occ in acd.Occurrences)
            {
                PartDocument doc = occ.Definition.Document;
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

            System.IO.File.WriteAllText("output.json", data);
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

        private static bool GetOnDemandFile(string name, string suffix, string headers, string responseFile, string content)
        {
            // writing a string (formatted according to ACESAPI format) to trace
            // invokes the onDemand call to get the desired optional input file
            LogTrace("!ACESAPI:acesHttpOperation({0},{1},{2},{3},{4})",
                name ?? "", suffix ?? "", headers ?? "", content ?? "", responseFile ?? "");

            // waiting for a control character indicating
            // that the download has successfully finished
            int idx = 0;
            while (true)
            {
                char ch = Convert.ToChar(Console.Read());
                // error
                if (ch == '\x3')
                {
                    return false;
                }
                // success
                else if (ch == '\n')
                {
                    return true;
                }

                // to many unexpected characters already read from console,
                // treating as other error / timeout
                if (idx >= 16)
                {
                    return false;
                }
                idx++;
            }
        }

        /// <summary>
        /// This will appear on the Design Automation output
        /// </summary>
        private static void LogTrace(string format, params object[] args) { Trace.TraceInformation(format, args); }
    }
}
