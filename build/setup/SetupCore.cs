// Mani Windows distribution boundary. SPDX-License-Identifier: GPL-3.0-only
// IINA's fixed snapshot distributes a macOS DMG; Windows installs the existing MSIX.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Mani.Setup
{
    internal sealed class DeploymentResult
    {
        public bool ok { get; set; }
        public string state { get; set; }
        public string version { get; set; }
        public string family { get; set; }
        public string message { get; set; }
        public string code { get; set; }
    }

    internal static class SetupCore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct VersionInfo
        {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
            public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
            public byte ProductType, Reserved;
        }
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref VersionInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        internal static bool SupportedPlatform()
        {
            var version = new VersionInfo { Size = (uint)Marshal.SizeOf(typeof(VersionInfo)) };
            ushort processMachine, nativeMachine;
            using (var process = Process.GetCurrentProcess())
                return RtlGetVersion(ref version) == 0 && version.Major >= 10 && version.Build >= 26100 && version.ProductType == 1 &&
                    IsWow64Process2(process.Handle, out processMachine, out nativeMachine) && nativeMachine == 0x8664;
        }

        internal static Stream Resource(string name)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mani.Setup." + name);
            if (stream == null) throw new InvalidDataException("Missing installer resource: " + name);
            return stream;
        }

        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static X509Certificate2 Certificate()
        {
            byte[] raw;
            using (var input = Resource("Certificate"))
            using (var output = new MemoryStream())
            {
                if (input.Length > 16384) throw new InvalidDataException("Invalid public certificate size.");
                input.CopyTo(output); raw = output.ToArray();
            }
            using (var stream = new MemoryStream(raw))
                if (Hash(stream) != BuildInfo.CertificateSha256) throw new InvalidDataException("Certificate checksum mismatch.");
            var certificate = new X509Certificate2(raw);
            try
            {
                if (certificate.HasPrivateKey || certificate.Subject != BuildInfo.Publisher ||
                    !String.Equals(certificate.Thumbprint, BuildInfo.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Certificate identity mismatch.");
                if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                    throw new InvalidDataException("The beta signing certificate is outside its validity period.");
                bool codeSigning = false, isAuthority = false;
                foreach (var extension in certificate.Extensions)
                {
                    var usage = extension as X509EnhancedKeyUsageExtension;
                    if (usage != null)
                        foreach (Oid oid in usage.EnhancedKeyUsages) codeSigning |= oid.Value == "1.3.6.1.5.5.7.3.3";
                    var constraints = extension as X509BasicConstraintsExtension;
                    if (constraints != null) isAuthority |= constraints.CertificateAuthority;
                }
                if (!codeSigning || isAuthority) throw new InvalidDataException("Expected an end-entity code signing certificate.");
                return certificate;
            }
            catch { certificate.Dispose(); throw; }
        }

        internal static bool CertificateTrusted()
        {
            using (var certificate = Certificate())
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
                catch (CryptographicException error)
                {
                    if (error.HResult == unchecked((int)0x80092004) || error.HResult == unchecked((int)0x80070002)) return false;
                    throw;
                }
                foreach (var item in store.Certificates)
                {
                    using (item)
                        if (String.Equals(item.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
                            Convert.ToBase64String(item.RawData) == Convert.ToBase64String(certificate.RawData)) return true;
                }
                return false;
            }
        }

        internal static void TrustEmbeddedCertificate()
        {
            // Elevated mode accepts no file paths and never writes to Root or CurrentUser.
            using (var certificate = Certificate())
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(certificate);
            }
            if (!CertificateTrusted()) throw new InvalidOperationException("Certificate trust could not be verified.");
        }

        internal static void RequestCertificateTrust()
        {
            if (CertificateTrusted()) return;
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--trust-certificate")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(start))
            {
                if (!process.WaitForExit(60000)) throw new TimeoutException("Certificate trust could not be confirmed in time.");
                if (process.ExitCode != 0) throw new InvalidOperationException("Certificate approval failed (" + process.ExitCode + ").");
            }
            if (!CertificateTrusted()) throw new InvalidOperationException("The certificate is still not trusted.");
        }

        internal static string CreateStagingDirectory()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Installer");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static string ExtractPackage(string directory, CancellationToken cancellation, Action<int> progress)
        {
            string path = Path.Combine(directory, "Mani.msix");
            using (var input = Resource("Msix"))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = SHA256.Create())
            {
                if (input.Length != BuildInfo.PackageBytes) throw new InvalidDataException("Package length mismatch.");
                var buffer = new byte[1024 * 1024];
                long copied = 0; int count, previous = -1;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    output.Write(buffer, 0, count); copied += count;
                    int percent = (int)(copied * 100 / BuildInfo.PackageBytes);
                    if (percent != previous) { progress(percent); previous = percent; }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (copied != BuildInfo.PackageBytes || BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant() != BuildInfo.PackageSha256)
                    throw new InvalidDataException("Installer payload checksum mismatch. Download the installer again.");
            }
            return path;
        }

        internal static void Cleanup(string directory)
        {
            if (String.IsNullOrEmpty(directory)) return;
            string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Installer")) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(directory);
            Guid id;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out id))
                throw new InvalidOperationException("Unexpected installer cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        internal static DeploymentResult Deploy(string mode, string path)
        {
            string script;
            using (var input = Resource("Install.ps1"))
            using (var reader = new StreamReader(input, Encoding.UTF8)) script = reader.ReadToEnd();
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            };
            // Do not inherit PowerShell 7 or user module paths into Windows PowerShell 5.1.
            start.EnvironmentVariables["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
            // Values are process environment data, never interpolated into PowerShell code.
            start.EnvironmentVariables["MANI_SETUP_MODE"] = mode;
            start.EnvironmentVariables["MANI_SETUP_PACKAGE"] = path ?? "";
            start.EnvironmentVariables["MANI_SETUP_NAME"] = BuildInfo.PackageName;
            start.EnvironmentVariables["MANI_SETUP_PUBLISHER"] = BuildInfo.Publisher;
            start.EnvironmentVariables["MANI_SETUP_VERSION"] = BuildInfo.PackageVersion;
            start.EnvironmentVariables["MANI_SETUP_SHA256"] = BuildInfo.PackageSha256;
            start.EnvironmentVariables["MANI_SETUP_THUMBPRINT"] = BuildInfo.CertificateThumbprint;
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(mode == "install" ? 600000 : 60000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException("Windows did not finish the operation in time. Check Windows Settings > Apps before retrying.");
                }
                string text = output.GetAwaiter().GetResult().Trim();
                string details = error.GetAwaiter().GetResult().Trim();
                DeploymentResult result;
                try { result = new JavaScriptSerializer().Deserialize<DeploymentResult>(text); }
                catch { throw new InvalidOperationException("Windows installation service did not return a result.\r\n" + details); }
                if (result == null || !result.ok || process.ExitCode != 0)
                    throw new InvalidOperationException(result == null ? details : result.code + "\r\n" + result.message);
                return result;
            }
        }
    }
}
