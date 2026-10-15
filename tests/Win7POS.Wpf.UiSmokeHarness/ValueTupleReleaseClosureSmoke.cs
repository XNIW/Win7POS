using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Invoked by the harness's early CLI path, before starting the WPF application.
    internal static class ValueTupleReleaseClosureSmoke
    {
        internal static string Run(string payloadDirectory, string receiptPath)
        {
            var watch = Stopwatch.StartNew();
            var facts = new Dictionary<string, object>();
            string phase = "controller_preflight";
            string probeJson = null;
            string payload = null;
            string receipt = null;
            string configHash = null;
            string failureStage = null;
            string failureType = null;
            string assembliesJson = "[]";
            string forwardsJson = "[]";
            string consumersJson = "{}";
            string requestedJson = "[]";
            AppDomain domain = null;
            Exception failure = null;
            bool unloaded = false;
            bool validatedReceipt = false;
            try
            {
                // Destination rejection is independent of runtime/GAC qualification.
                // Do not inspect or create anything at the receipt path before separation.
                try
                {
                    payload = ValueTupleClosureSupport.LocalFullPath(payloadDirectory);
                    receipt = ValueTupleClosureSupport.LocalFullPath(receiptPath);
                    ValueTupleClosureSupport.QualifyReceiptDestination(payload, receipt);
                    validatedReceipt = true;
                }
                catch
                {
                    // Fixed marker; no path or incidental runtime error can masquerade as this rejection.
                    throw new ValueTupleReceiptDestinationException();
                }
                facts = ValueTupleClosureSupport.RequireRuntime();
                string config = ValueTupleClosureSupport.PayloadFile(payload, "Win7POS.Wpf.exe.config");
                configHash = ValueTupleClosureSupport.Hash(config);
                phase = "controller_config";
                ValueTupleClosureSupport.RequireExactRedirect(config);
                string harness = ValueTupleClosureSupport.LocalFullPath(
                    typeof(ValueTupleReleaseClosureProbe).Assembly.Location);
                ValueTupleClosureSupport.RequireFileWithoutReparsePoints(harness);
                var setup = new AppDomainSetup
                {
                    ApplicationBase = payload,
                    ConfigurationFile = config,
                    PrivateBinPath = null,
                    ShadowCopyFiles = "false",
                    DisallowCodeDownload = true,
                    TargetFrameworkName = ".NETFramework,Version=v4.8"
                };
                phase = "isolated_appdomain";
                domain = AppDomain.CreateDomain(
                    "ValueTupleReleaseClosure-" + Guid.NewGuid().ToString("N"), null, setup);
                var probe = (ValueTupleReleaseClosureProbe)domain.CreateInstanceFromAndUnwrap(
                    harness, typeof(ValueTupleReleaseClosureProbe).FullName);
                try
                {
                    probeJson = probe.Execute(payload);
                }
                catch
                {
                    // Retrieve partial static/runtime observations before unloading the domain.
                    try { probeJson = probe.GetLastReceiptJson(); } catch { }
                    try { failureStage = probe.GetFailureStage(); failureType = probe.GetFailureType(); } catch { }
                    throw;
                }
                finally
                {
                    try
                    {
                        assembliesJson = probe.GetEvidencePartJson("assemblies");
                        forwardsJson = probe.GetEvidencePartJson("forwarded_types");
                        consumersJson = probe.GetEvidencePartJson("consumers");
                        requestedJson = probe.GetEvidencePartJson("requested_facades");
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException("Isolated evidence collection failed.", error);
                    }
                }
                phase = "appdomain_unload";
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (domain != null)
                {
                    try
                    {
                        AppDomain.Unload(domain);
                        unloaded = true;
                    }
                    catch (Exception error)
                    {
                        failure = failure == null ? error : new AggregateException(failure, error);
                        phase = "appdomain_unload";
                        failureStage = phase;
                        failureType = error.GetType().FullName;
                    }
                }
            }

            if (!validatedReceipt)
            {
                // An unsafe/unqualified destination has no permitted receipt side effect.
                // Keep the specific exception type visible to the early CLI and negative runner.
                if (failure is ValueTupleReceiptDestinationException rejection) throw rejection;
                throw new ValueTupleReceiptDestinationException();
            }
            bool passed = failure == null && unloaded && probeJson != null;
            var document = new Dictionary<string, object>
            {
                ["schema"] = "WIN7POS_PACK_VALUE_TUPLE_CLOSURE_V1",
                ["status"] = passed ? "PASS" : "FAIL",
                ["stage"] = passed ? "complete" : failureStage ?? phase,
                ["failStage"] = passed ? null : failureStage ?? phase,
                ["failType"] = passed ? null : failureType ?? ValueTupleClosureSupport.InnermostType(failure),
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["elapsed_ms"] = watch.ElapsedMilliseconds,
                ["payload_directory"] = payload,
                ["config_sha256"] = configHash,
                ["controller_runtime"] = facts,
                ["isolated_domain_unloaded"] = unloaded,
                ["assemblies"] = new ValueTupleClosureJson.Raw(assembliesJson),
                ["forwarded_types"] = new ValueTupleClosureJson.Raw(forwardsJson),
                ["consumers"] = new ValueTupleClosureJson.Raw(consumersJson),
                ["requested_facades"] = new ValueTupleClosureJson.Raw(requestedJson),
                ["probe"] = new ValueTupleClosureJson.Raw(probeJson ?? "null"),
                ["error"] = failure == null ? null : ValueTupleClosureSupport.Error(failure)
            };
            // A failed receipt write also throws. It can never produce a successful return.
            ValueTupleClosureSupport.WriteNewReceipt(receipt ?? receiptPath,
                ValueTupleClosureJson.Encode(document));
            if (!passed)
                throw new InvalidOperationException("Packed ValueTuple closure smoke failed; receipt preserved.", failure);
            return "PASS packed_valuetuple_closure=true appdomain_unloaded=true consumers=3 forwarded_types=11";
        }
    }

    internal sealed class ValueTupleReceiptDestinationException : ArgumentException
    {
        internal const string Marker = "VALUETUPLE_RECEIPT_DESTINATION_REJECTED";
        internal ValueTupleReceiptDestinationException() : base(Marker) { }
    }

    // Public because the controller creates this type across the AppDomain boundary.
    // No Excel/CodePages types occur in this type's signatures or static initializers.
    public sealed class ValueTupleReleaseClosureProbe : MarshalByRefObject
    {
        private string lastReceiptJson;
        private string failureStage;
        private string failureType;
        private Dictionary<string, object> evidence;
        private static readonly string[] ExpectedForwardedTypes =
        {
            "System.Runtime.CompilerServices.TupleElementNamesAttribute",
            "System.TupleExtensions",
            "System.ValueTuple",
            "System.ValueTuple`1",
            "System.ValueTuple`2",
            "System.ValueTuple`3",
            "System.ValueTuple`4",
            "System.ValueTuple`5",
            "System.ValueTuple`6",
            "System.ValueTuple`7",
            "System.ValueTuple`8"
        };

        public override object InitializeLifetimeService() { return null; }
        public string GetLastReceiptJson() { return lastReceiptJson; }
        public string GetFailureStage() { return failureStage; }
        public string GetFailureType() { return failureType; }
        public string GetEvidencePartJson(string key)
        {
            ValueTupleClosureSupport.Require(key == "assemblies" || key == "forwarded_types" ||
                key == "consumers" || key == "requested_facades", "Unknown evidence part.");
            return ValueTupleClosureJson.Encode(evidence[key]);
        }

        public string Execute(string payloadDirectory)
        {
            var record = new Dictionary<string, object>
            {
                ["status"] = "FAIL",
                ["assemblies"] = new List<object>(),
                ["requested_facades"] = new List<object>(),
                ["forwarded_types"] = new List<object>(),
                ["consumers"] = new Dictionary<string, object>()
            };
            evidence = record;
            string phase = "probe_preflight";
            try
            {
                string payload = ValueTupleClosureSupport.LocalFullPath(payloadDirectory);
                record["runtime"] = ValueTupleClosureSupport.RequireRuntime();
                ValueTupleClosureSupport.RequireDirectoryWithoutReparsePoints(payload);
                ValueTupleClosureSupport.Require(
                    ValueTupleClosureSupport.SamePath(AppDomain.CurrentDomain.BaseDirectory, payload),
                    "The child ApplicationBase is not the payload directory.");
                string config = ValueTupleClosureSupport.PayloadFile(payload, "Win7POS.Wpf.exe.config");
                ValueTupleClosureSupport.Require(
                    ValueTupleClosureSupport.SamePath(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile, config),
                    "The child ConfigurationFile is not the payload config.");
                record["application_base"] = AppDomain.CurrentDomain.BaseDirectory;
                record["configuration_file"] = config;
                record["config_sha256"] = ValueTupleClosureSupport.Hash(config);
                phase = "probe_config";
                ValueTupleClosureSupport.RequireExactRedirect(config);
                CheckTrackedLocations(payload);

                phase = "facade_binding";
                var requested = (List<object>)record["requested_facades"];
                Assembly first = null;
                foreach (string version in new[] { "4.0.3.0", "4.0.5.0" })
                {
                    Assembly facade = Assembly.Load("System.ValueTuple, Version=" + version +
                        ", Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
                    RequirePayloadAssembly(facade, payload, "System.ValueTuple", "4.0.5.0", "cc7b13ffcd2ddd51");
                    if (first != null)
                        ValueTupleClosureSupport.Require(ReferenceEquals(first, facade),
                            "Both ValueTuple identities must bind to the same payload assembly.");
                    first = facade;
                    requested.Add(new Dictionary<string, object>
                    {
                        ["requested_version"] = version,
                        ["resolved"] = ValueTupleClosureSupport.Describe(facade)
                    });
                }

                phase = "forwarded_type_providers";
                Assembly mscorlib = typeof(object).Assembly;
                ValueTupleClosureSupport.RequireFrameworkMscorlib(mscorlib);
                var provider = ValueTupleClosureSupport.Describe(mscorlib);
                record["framework_type_provider"] = provider;
                // .NET Framework 4.8 exposes forwarded types through Assembly.GetType.
                // The complete PE set is frozen by the exact facade byte pin; this is named resolution.
                ValueTupleClosureSupport.Require(
                    ValueTupleClosureSupport.Hash(first.Location) == "7cd7ac314a72774b5e2255fd798f876926c63e19f9b8304cae25df012beabbcb",
                    "The facade bytes differ from the reviewed complete 11-forwarder PE set.");
                record["forwarded_type_verification"] = "expected11_named_resolution_plus_pinned_PE_set";
                record["facade_pe_set_pin_sha256"] = "7cd7ac314a72774b5e2255fd798f876926c63e19f9b8304cae25df012beabbcb";
                var actualNames = new List<string>();
                var forwards = (List<object>)record["forwarded_types"];
                foreach (string name in ExpectedForwardedTypes)
                {
                    Type type = first.GetType(name, true, false);
                    ValueTupleClosureSupport.Require(type.FullName == name,
                        "Named facade type resolution returned a different type: " + name);
                    actualNames.Add(type.FullName);
                    ValueTupleClosureSupport.Require(ReferenceEquals(type.Assembly, mscorlib),
                        "Forwarded type is not provided by framework mscorlib: " + type.FullName);
                    forwards.Add(new Dictionary<string, object>
                    {
                        ["type"] = type.FullName,
                        ["provider"] = provider
                    });
                }
                actualNames.Sort(StringComparer.Ordinal);
                var expectedNames = new List<string>(ExpectedForwardedTypes);
                expectedNames.Sort(StringComparer.Ordinal);
                ValueTupleClosureSupport.Require(actualNames.Count == 11, "Exactly 11 expected named type resolutions are required.");
                for (int i = 0; i < expectedNames.Count; i++)
                    ValueTupleClosureSupport.Require(actualNames[i] == expectedNames[i],
                        "The expected named type resolution set is different from the reviewed facade.");

                phase = "consumer_load_codepages";
                Assembly codePages = LoadConsumer(payload, "System.Text.Encoding.CodePages", "10.0.0.12", "b03f5f7f11d50a3a");
                phase = "consumer_load_excel";
                Assembly excel = LoadConsumer(payload, "ExcelDataReader", "3.9.0.0", "93517dbe6a4012fa");
                phase = "consumer_load_dataset";
                Assembly dataSet = LoadConsumer(payload, "ExcelDataReader.DataSet", "3.9.0.0", "93517dbe6a4012fa");
                var assemblies = (List<object>)record["assemblies"];
                assemblies.Add(ValueTupleClosureSupport.Describe(first));
                assemblies.Add(ValueTupleClosureSupport.Describe(codePages));
                assemblies.Add(ValueTupleClosureSupport.Describe(excel));
                assemblies.Add(ValueTupleClosureSupport.Describe(dataSet));
                CheckTrackedLocations(payload);

                var consumers = (Dictionary<string, object>)record["consumers"];
                phase = "codepages_roundtrip";
                consumers["System.Text.Encoding.CodePages"] = ExerciseCodePages(codePages);
                CheckTrackedLocations(payload);
                phase = "excel_openxml_reader";
                byte[] workbook = Convert.FromBase64String(OwnedWorkbookBase64);
                record["owned_workbook_sha256"] = ValueTupleClosureSupport.Hash(workbook);
                using (var stream = new MemoryStream(workbook, false))
                using (IDataReader reader = OpenWorkbook(excel, stream))
                {
                    ValueTupleClosureSupport.Require(reader.FieldCount == 2, "Synthetic workbook must have 2 columns.");
                    int row = 0;
                    while (reader.Read())
                    {
                        ValueTupleClosureSupport.Require(row < 2, "Unexpected workbook row.");
                        CheckCells(row, reader.GetValue(0), reader.GetValue(1));
                        row++;
                    }
                    ValueTupleClosureSupport.Require(row == 2 && !reader.NextResult(),
                        "Synthetic workbook must contain exactly one 2-row sheet.");
                    consumers["ExcelDataReader"] = new Dictionary<string, object>
                    { ["status"] = "PASS", ["rows"] = row, ["columns"] = 2 };
                }
                CheckTrackedLocations(payload);
                phase = "excel_dataset";
                using (var stream = new MemoryStream(workbook, false))
                using (IDataReader reader = OpenWorkbook(excel, stream))
                {
                    Type readerType = excel.GetType("ExcelDataReader.IExcelDataReader", true);
                    Type configType = dataSet.GetType("ExcelDataReader.ExcelDataSetConfiguration", true);
                    Type extensions = dataSet.GetType("ExcelDataReader.ExcelDataReaderExtensions", true);
                    MethodInfo method = extensions.GetMethod("AsDataSet", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { readerType, configType }, null);
                    ValueTupleClosureSupport.Require(method != null, "AsDataSet API is missing.");
                    using (var result = (DataSet)method.Invoke(null,
                        new[] { (object)reader, Activator.CreateInstance(configType) }))
                    {
                        ValueTupleClosureSupport.Require(result.Tables.Count == 1,
                            "AsDataSet must return exactly one table.");
                        DataTable table = result.Tables[0];
                        ValueTupleClosureSupport.Require(table.TableName == "OwnedSynthetic" &&
                            table.Rows.Count == 2 && table.Columns.Count == 2,
                            "AsDataSet dimensions or sheet name are incorrect.");
                        for (int row = 0; row < 2; row++)
                            CheckCells(row, table.Rows[row][0], table.Rows[row][1]);
                        consumers["ExcelDataReader.DataSet"] = new Dictionary<string, object>
                        { ["status"] = "PASS", ["rows"] = 2, ["columns"] = 2, ["cells_checked"] = 4 };
                    }
                }
                CheckTrackedLocations(payload);
                phase = "complete";
                record["status"] = "PASS";
                record["stage"] = phase;
                record["failStage"] = null;
                record["failType"] = null;
                lastReceiptJson = ValueTupleClosureJson.Encode(record);
                return lastReceiptJson;
            }
            catch (Exception error)
            {
                record["status"] = "FAIL";
                failureStage = phase;
                failureType = ValueTupleClosureSupport.InnermostType(error);
                record["stage"] = phase;
                record["failStage"] = phase;
                record["failType"] = failureType;
                record["error"] = ValueTupleClosureSupport.Error(error);
                lastReceiptJson = ValueTupleClosureJson.Encode(record);
                throw;
            }
        }

        private static object ExerciseCodePages(Assembly assembly)
        {
            Type type = assembly.GetType("System.Text.CodePagesEncodingProvider", true);
            PropertyInfo instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            ValueTupleClosureSupport.Require(instance != null, "CodePages provider Instance API is missing.");
            object provider = instance.GetValue(null, null);
            MethodInfo getEncoding = type.GetMethod("GetEncoding", new[] { typeof(int) });
            ValueTupleClosureSupport.Require(getEncoding != null, "CodePages GetEncoding(int) API is missing.");
            var cases = new List<object>();
            int[] numbers = { 1252, 936 };
            string[] texts = { "caff\u00e8 \u20ac", "\u5546\u5e97\u5546\u54c1" };
            for (int i = 0; i < numbers.Length; i++)
            {
                var encoding = (Encoding)getEncoding.Invoke(provider, new object[] { numbers[i] });
                ValueTupleClosureSupport.Require(encoding != null && encoding.CodePage == numbers[i],
                    "The requested encoding was not returned by the payload CodePages provider.");
                byte[] encoded = encoding.GetBytes(texts[i]);
                ValueTupleClosureSupport.Require(encoded.Length > 0 && encoding.GetString(encoded) == texts[i],
                    "CodePages roundtrip failed.");
                cases.Add(new Dictionary<string, object>
                { ["codepage"] = numbers[i], ["status"] = "PASS", ["encoded_bytes"] = encoded.Length,
                    ["encoding_type"] = encoding.GetType().FullName });
            }
            return new Dictionary<string, object> { ["status"] = "PASS", ["roundtrips"] = cases };
        }

        private static IDataReader OpenWorkbook(Assembly assembly, Stream stream)
        {
            Type factory = assembly.GetType("ExcelDataReader.ExcelReaderFactory", true);
            Type configType = assembly.GetType("ExcelDataReader.ExcelReaderConfiguration", true);
            object config = Activator.CreateInstance(configType);
            MethodInfo method = factory.GetMethod("CreateOpenXmlReader", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Stream), configType }, null);
            ValueTupleClosureSupport.Require(method != null, "CreateOpenXmlReader API is missing.");
            var reader = method.Invoke(null, new[] { (object)stream, config }) as IDataReader;
            ValueTupleClosureSupport.Require(reader != null, "OpenXml reader does not implement IDataReader.");
            return reader;
        }

        private static void CheckCells(int row, object first, object second)
        {
            string a = row == 0 ? "alpha" : "beta";
            string b = row == 0 ? "one" : "two";
            ValueTupleClosureSupport.Require(first is string && second is string &&
                (string)first == a && (string)second == b, "Synthetic workbook cell mismatch.");
        }

        private static Assembly LoadConsumer(string payload, string name, string version, string token)
        {
            Assembly assembly = Assembly.Load(name + ", Version=" + version +
                ", Culture=neutral, PublicKeyToken=" + token);
            RequirePayloadAssembly(assembly, payload, name, version, token);
            string expectedReferenceVersion = name == "System.Text.Encoding.CodePages" ? "4.0.5.0" : "4.0.3.0";
            int references = 0;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (!string.Equals(reference.Name, "System.ValueTuple", StringComparison.OrdinalIgnoreCase)) continue;
                references++;
                ValueTupleClosureSupport.Require(reference.Name == "System.ValueTuple" &&
                    reference.Version == new Version(expectedReferenceVersion) &&
                    ValueTupleClosureSupport.Token(reference) == "cc7b13ffcd2ddd51",
                    "Consumer's actual ValueTuple AssemblyRef is not the reviewed identity: " + name);
            }
            ValueTupleClosureSupport.Require(references == 1,
                "Consumer must have exactly one reviewed ValueTuple AssemblyRef: " + name);
            return assembly;
        }

        private static void RequirePayloadAssembly(Assembly assembly, string payload,
            string name, string version, string token)
        {
            AssemblyName actual = assembly.GetName();
            string expected = ValueTupleClosureSupport.PayloadFile(payload, name + ".dll");
            ValueTupleClosureSupport.Require(actual.Name == name && actual.Version == new Version(version) &&
                ValueTupleClosureSupport.Token(actual) == token && !assembly.GlobalAssemblyCache &&
                ValueTupleClosureSupport.SamePath(assembly.Location, expected),
                "Assembly did not resolve to the exact reviewed payload identity/location: " + name);
        }

        private static bool IsTracked(string name)
        {
            return string.Equals(name, "System.ValueTuple", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ExcelDataReader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ExcelDataReader.DataSet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "System.Text.Encoding.CodePages", StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckTrackedLocations(string payload)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!IsTracked(name)) continue;
                string version = name == "System.ValueTuple" ? "4.0.5.0" :
                    name == "System.Text.Encoding.CodePages" ? "10.0.0.12" : "3.9.0.0";
                string token = name == "System.ValueTuple" ? "cc7b13ffcd2ddd51" :
                    name == "System.Text.Encoding.CodePages" ? "b03f5f7f11d50a3a" : "93517dbe6a4012fa";
                RequirePayloadAssembly(assembly, payload, name, version, token);
            }
        }

        // Owned synthetic XLSX, one sheet, four string cells, generated without customer data.
        private const string OwnedWorkbookBase64 = "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";
    }

    internal static class ValueTupleClosureSupport
    {
        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        internal static Dictionary<string, object> RequireRuntime()
        {
            Require(Environment.OSVersion.Platform == PlatformID.Win32NT, "Windows CLR is required.");
            Require(IntPtr.Size == 4, "The process must be x86.");
            Assembly core = typeof(object).Assembly;
            Require(core.GetName().Name == "mscorlib" && Environment.Version.Major == 4 &&
                core.ImageRuntimeVersion.StartsWith("v4.0", StringComparison.Ordinal),
                "The process must run on .NET Framework CLR4, not CoreCLR.");
            int release;
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", false))
            {
                object value = key == null ? null : key.GetValue("Release", null);
                Require(value is int, "Installed .NET Framework Release registry value is absent.");
                release = (int)value;
            }
            Require(release >= 528040, "Installed .NET Framework must be 4.8 or later (Release >= 528040).");
            RequireFrameworkMscorlib(core);
            return new Dictionary<string, object>
            {
                ["framework"] = ".NETFramework",
                ["clr_version"] = Environment.Version.ToString(),
                ["process_bits"] = IntPtr.Size * 8,
                ["installed_release"] = release,
                ["os_version"] = Environment.OSVersion.VersionString,
                ["framework_runtime_directory"] = RuntimeEnvironment.GetRuntimeDirectory(),
                ["mscorlib"] = Describe(core)
            };
        }

        internal static void RequireFrameworkMscorlib(Assembly assembly)
        {
            AssemblyName name = assembly.GetName();
            string expected = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "mscorlib.dll");
            Require(name.Name == "mscorlib" && name.Version == new Version("4.0.0.0") &&
                Token(name) == "b77a5c561934e089" && assembly.GlobalAssemblyCache &&
                SamePath(assembly.Location, expected),
                "Tuple provider must be framework/GAC mscorlib in the CLR runtime directory.");
        }

        internal static string LocalFullPath(string path)
        {
            Require(!string.IsNullOrWhiteSpace(path), "A local absolute path is required.");
            string full = Path.GetFullPath(path);
            Require(Path.IsPathRooted(path) && !full.StartsWith(@"\\", StringComparison.Ordinal),
                "UNC/device or relative paths are not allowed.");
            Require(new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Network,
                "Mapped network paths are not allowed.");
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full :
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static bool SamePath(string first, string second)
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'),
                Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWithin(string path, string directory)
        {
            return SamePath(path, directory) || Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static void RequireDirectoryWithoutReparsePoints(string path)
        {
            Require(Directory.Exists(path), "Payload directory does not exist.");
            RequireNoReparsePoints(path);
        }

        internal static void RequireFileWithoutReparsePoints(string path)
        {
            Require(File.Exists(path), "Required local file does not exist: " + path);
            RequireNoReparsePoints(path);
        }

        private static void RequireNoReparsePoints(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "Reparse-point fallback is not allowed: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        internal static string PayloadFile(string payload, string name)
        {
            string path = Path.Combine(payload, name);
            RequireFileWithoutReparsePoints(path);
            return path;
        }

        internal static void QualifyReceiptDestination(string payload, string receipt)
        {
            // This lexical boundary must precede all receipt file/parent I/O.
            Require(!IsWithin(receipt, payload), "Receipt must be outside the payload directory.");
            RequireDirectoryWithoutReparsePoints(payload);
            string parent = Path.GetDirectoryName(receipt);
            Require(!string.IsNullOrEmpty(parent), "Receipt parent is absent.");

            // Check the existing ancestor chain before creating any missing parent directories.
            string ancestor = parent;
            while (!Directory.Exists(ancestor))
            {
                Require(!EntryExists(ancestor), "Receipt parent path is occupied by a non-directory entry.");
                ancestor = Path.GetDirectoryName(ancestor);
                Require(!string.IsNullOrEmpty(ancestor), "Receipt has no qualified existing ancestor.");
            }
            RequireDirectoryWithoutReparsePoints(ancestor);
            Require(!EntryExists(receipt), "Receipt must be fresh; existing evidence is preserved.");
            Directory.CreateDirectory(parent);
            RequireDirectoryWithoutReparsePoints(parent);
            Require(!EntryExists(receipt), "Receipt path became occupied before qualification completed.");
        }

        private static bool EntryExists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        internal static void RequireExactRedirect(string configuration)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(configuration, settings)) document.Load(reader);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("a", "urn:schemas-microsoft-com:asm.v1");
            XmlNodeList entries = document.SelectNodes("/configuration/runtime/a:assemblyBinding/a:dependentAssembly", ns);
            int matches = 0;
            foreach (XmlNode entry in entries)
            {
                XmlElement identity = entry.SelectSingleNode("a:assemblyIdentity", ns) as XmlElement;
                if (identity == null || !string.Equals(identity.GetAttribute("name"),
                    "System.ValueTuple", StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                XmlNodeList redirects = entry.SelectNodes("a:bindingRedirect", ns);
                XmlElement redirect = redirects.Count == 1 ? redirects[0] as XmlElement : null;
                Require(identity.GetAttribute("name") == "System.ValueTuple" &&
                    identity.GetAttribute("publicKeyToken") == "cc7b13ffcd2ddd51" &&
                    identity.GetAttribute("culture") == "neutral" && redirect != null &&
                    redirect.GetAttribute("oldVersion") == "0.0.0.0-4.0.5.0" &&
                    redirect.GetAttribute("newVersion") == "4.0.5.0" &&
                    entry.SelectNodes("a:codeBase|a:publisherPolicy", ns).Count == 0,
                    "Exactly the reviewed ValueTuple <=4.0.5 ->4.0.5 redirect is required.");
            }
            Require(matches == 1, "Exactly one ValueTuple binding policy is required.");
            Require(document.SelectNodes("/configuration/runtime/a:assemblyBinding/a:probing", ns).Count == 0,
                "Payload config must not add a private probing fallback.");
        }

        internal static string Token(AssemblyName name)
        {
            byte[] token = name.GetPublicKeyToken();
            return token == null ? "" : BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
        }

        internal static Dictionary<string, object> Describe(Assembly assembly)
        {
            AssemblyName name = assembly.GetName();
            var tupleReferences = new List<object>();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (string.Equals(reference.Name, "System.ValueTuple", StringComparison.OrdinalIgnoreCase))
                    tupleReferences.Add(new Dictionary<string, object>
                    {
                        ["full_name"] = reference.FullName,
                        ["version"] = reference.Version.ToString(),
                        ["public_key_token"] = Token(reference)
                    });
            }
            return new Dictionary<string, object>
            {
                ["name"] = name.Name, ["full_name"] = assembly.FullName,
                ["version"] = name.Version.ToString(), ["public_key_token"] = Token(name),
                ["location"] = assembly.Location, ["sha256"] = Hash(assembly.Location),
                ["gac"] = assembly.GlobalAssemblyCache,
                ["valuetuple_references"] = tupleReferences
            };
        }

        internal static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static object Error(Exception error)
        {
            var errors = new List<object>();
            for (int i = 0; error != null && i < 8; i++, error = error.InnerException)
                errors.Add(new Dictionary<string, object>
                { ["type"] = error.GetType().FullName, ["message"] = error.Message });
            return errors;
        }

        internal static string InnermostType(Exception error)
        {
            if (error == null) return "System.InvalidOperationException";
            for (int i = 0; error.InnerException != null && i < 8; i++) error = error.InnerException;
            return error.GetType().FullName;
        }

        internal static void WriteNewReceipt(string path, string json)
        {
            string full = LocalFullPath(path);
            string parent = Path.GetDirectoryName(full);
            RequireDirectoryWithoutReparsePoints(parent);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(json + Environment.NewLine);
            using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
        }
    }

    // Small JSON writer for an evidence-only graph of primitives, dictionaries and lists.
    internal static class ValueTupleClosureJson
    {
        internal sealed class Raw
        {
            internal readonly string Text;
            internal Raw(string text) { Text = text; }
        }

        internal static string Encode(object value)
        {
            var builder = new StringBuilder();
            Append(builder, value);
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("null"); return; }
            if (value is Raw raw) { builder.Append(raw.Text); return; }
            if (value is string text) { Quote(builder, text); return; }
            if (value is bool flag) { builder.Append(flag ? "true" : "false"); return; }
            if (value is int || value is long)
            { builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is IDictionary<string, object> map)
            {
                builder.Append('{'); bool first = true;
                foreach (var pair in map)
                {
                    if (!first) builder.Append(','); first = false;
                    Quote(builder, pair.Key); builder.Append(':'); Append(builder, pair.Value);
                }
                builder.Append('}'); return;
            }
            if (value is IEnumerable<object> list)
            {
                builder.Append('['); bool first = true;
                foreach (object item in list)
                { if (!first) builder.Append(','); first = false; Append(builder, item); }
                builder.Append(']'); return;
            }
            throw new InvalidOperationException("Unsupported evidence JSON value type: " + value.GetType().FullName);
        }

        private static void Quote(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (char ch in text)
            {
                if (ch == '"') builder.Append("\\\"");
                else if (ch == '\\') builder.Append("\\\\");
                else if (ch < 0x20 || char.IsSurrogate(ch) || ch == '\u2028' || ch == '\u2029')
                    builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else builder.Append(ch);
            }
            builder.Append('"');
        }
    }
}
