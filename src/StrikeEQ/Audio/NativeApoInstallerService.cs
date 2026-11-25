using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace StrikeEQ.Audio;

/// <summary>
/// Installs, registers, binds, and cleanly uninstalls StrikeEQ's own native user-mode
/// Windows Audio Processing Object (StrikeEQ.Apo.dll) on any playback endpoint without
/// needing Equalizer APO or VB-Cable.
/// </summary>
public sealed class NativeApoInstallerService
{
    public const string StrikeEqApoClsid = "{7A94E2C1-5F83-4D62-9B18-3C4A7E2D8F01}";
    public const string DllFileName = "FootStepEQ.Apo.dll";

    // Windows MMDevice FxProperties property keys
    private const string PKEY_FX_Association              = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0";
    private const string PKEY_FX_EndpointEffectClsid      = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
    private const string PKEY_EFX_ProcessingModes         = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
    private const string PKEY_AudioEndpoint_Disable_SysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string AUDIO_SIGNALPROCESSINGMODE_DEFAULT = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    private const string KSNODETYPE_ANY                   = "{00000000-0000-0000-0000-000000000000}";

    public static string InstalledDllPath =>
        Path.Combine(NativeApoSharedMemory.SharedDirectory, DllFileName);

    public static string? LocateSourceDll()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, DllFileName),
            InstalledDllPath,
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "publish", DllFileName)
        ];

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public static bool IsComRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{StrikeEqApoClsid}\InprocServer32");
            string? path = key?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static string? ExtractEndpointGuid(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        // WASAPI endpoint IDs look like "{0.0.0.00000000}.{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}"
        int lastBrace = deviceId.LastIndexOf('{');
        if (lastBrace >= 0 && deviceId.EndsWith('}'))
            return deviceId[lastBrace..];
        return deviceId;
    }

    public static bool IsInstalledOnEndpoint(string? deviceId)
    {
        string? guid = ExtractEndpointGuid(deviceId);
        if (string.IsNullOrWhiteSpace(guid)) return false;

        try
        {
            string path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties";
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            string? efx = key?.GetValue(PKEY_FX_EndpointEffectClsid) as string;
            return string.Equals(efx, StrikeEqApoClsid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Launches an elevated instance of StrikeEQ.exe with --install-apo or --uninstall-apo
    /// so the user only gets a single UAC prompt when configuring an audio endpoint.
    /// </summary>
    public static bool RunElevatedAction(bool install, string deviceId, out string message)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            message = "Não foi possível localizar o executável do StrikeEQ para elevação.";
            return false;
        }

        string argFlag = install ? "--install-apo" : "--uninstall-apo";
        string resultFile = Path.Combine(Path.GetTempPath(), $"strikeeq_apo_result_{Guid.NewGuid():N}.txt");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{argFlag} \"{deviceId}\" \"{resultFile}\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                message = "Falha ao iniciar o instalador elevado.";
                return false;
            }

            proc.WaitForExit(35000);

            if (File.Exists(resultFile))
            {
                string content = File.ReadAllText(resultFile).Trim();
                try { File.Delete(resultFile); } catch { }
                bool ok = proc.ExitCode == 0;
                message = string.IsNullOrWhiteSpace(content)
                    ? (ok ? "Operação concluída com sucesso." : "Falha na instalação da APO.")
                    : content;
                return ok;
            }

            message = proc.ExitCode == 0
                ? "StrikeEQ APO configurado com sucesso."
                : $"Instalador retornou código {proc.ExitCode}.";
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "Permissão de Administrador (UAC) cancelada pelo usuário.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Erro ao elevar instalador: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Performs the full native APO installation (must run as Administrator).
    /// </summary>
    public static bool InstallOnEndpointElevated(string deviceId, out string message)
    {
        if (!IsElevated())
        {
            message = "Permissão de Administrador necessária.";
            return false;
        }

        string? guid = ExtractEndpointGuid(deviceId);
        if (string.IsNullOrWhiteSpace(guid))
        {
            message = "ID de dispositivo de saída inválido.";
            return false;
        }

        string? sourceDll = LocateSourceDll();
        if (sourceDll is null)
        {
            message = $"Arquivo {DllFileName} não encontrado ao lado do StrikeEQ.exe.";
            return false;
        }

        try
        {
            // 1. Prepare C:\ProgramData\StrikeEQ with Users + LOCAL SERVICE access
            Directory.CreateDirectory(NativeApoSharedMemory.SharedDirectory);
            GrantDirectoryPermissions(NativeApoSharedMemory.SharedDirectory);

            // Copy StrikeEQ.Apo.dll (stop AudioSrv first if locked by previous version)
            bool dllCopied = TryCopyDll(sourceDll, InstalledDllPath);
            if (!dllCopied)
            {
                RestartAudioService(stopOnly: true);
                Thread.Sleep(600);
                File.Copy(sourceDll, InstalledDllPath, overwrite: true);
            }

            // Initialize shared state file if needed so audiodg.exe finds it immediately
            using (var shm = new NativeApoSharedMemory())
            {
                shm.TryOpen();
            }
            GrantFilePermissions(NativeApoSharedMemory.SharedFilePath);

            // 2. Register COM Server + AudioProcessingObjects registry
            var reg = Process.Start(new ProcessStartInfo
            {
                FileName = "regsvr32.exe",
                Arguments = $"/s \"{InstalledDllPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            reg?.WaitForExit(8000);
            if (reg is null || reg.ExitCode != 0)
            {
                message = $"regsvr32 falhou ao registrar StrikeEQ.Apo.dll (código {reg?.ExitCode}).";
                return false;
            }

            // 3. Allow audiodg.exe to load our custom User-Mode APO DLL without test-signing
            using (var audioKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", writable: true))
            {
                audioKey?.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
            }

            // 4. Take ownership & grant Administrators access to endpoint FxProperties
            string fxRegSubKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties";
            string endpointRegSubKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}";
            TakeRegistryKeyOwnership("MACHINE\\" + endpointRegSubKey);
            TakeRegistryKeyOwnership("MACHINE\\" + fxRegSubKey);

            // Backup existing FxProperties before modifying
            BackupFxProperties(guid, fxRegSubKey);

            using (var fxKey = Registry.LocalMachine.CreateSubKey(fxRegSubKey, writable: true))
            {
                if (fxKey is null)
                {
                    message = "Não foi possível abrir FxProperties do dispositivo no Registro.";
                    return false;
                }

                if (fxKey.GetValue(PKEY_FX_Association) is null)
                    fxKey.SetValue(PKEY_FX_Association, KSNODETYPE_ANY, RegistryValueKind.String);

                fxKey.SetValue(PKEY_FX_EndpointEffectClsid, StrikeEqApoClsid, RegistryValueKind.String);
                fxKey.SetValue(PKEY_EFX_ProcessingModes, new[] { AUDIO_SIGNALPROCESSINGMODE_DEFAULT }, RegistryValueKind.MultiString);
                fxKey.SetValue(PKEY_AudioEndpoint_Disable_SysFx, 0, RegistryValueKind.DWord);
            }

            // 5. Restart Windows Audio Service so audiodg.exe loads StrikeEQ.Apo.dll immediately
            RestartAudioService(stopOnly: false);

            message = "StrikeEQ Native APO instalado e ativado neste fone com sucesso! (Latência ~0 ms)";
            return true;
        }
        catch (Exception ex)
        {
            RestartAudioService(stopOnly: false);
            message = $"Erro ao instalar StrikeEQ Native APO: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Restores original endpoint FxProperties and restarts AudioSrv (must run as Administrator).
    /// </summary>
    public static bool UninstallFromEndpointElevated(string deviceId, out string message)
    {
        if (!IsElevated())
        {
            message = "Permissão de Administrador necessária.";
            return false;
        }

        string? guid = ExtractEndpointGuid(deviceId);
        if (string.IsNullOrWhiteSpace(guid))
        {
            message = "ID de dispositivo inválido.";
            return false;
        }

        try
        {
            string fxRegSubKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties";
            TakeRegistryKeyOwnership("MACHINE\\" + fxRegSubKey);

            RestoreFxProperties(guid, fxRegSubKey);
            RestartAudioService(stopOnly: false);

            message = "StrikeEQ Native APO removido deste dispositivo e configuração original restaurada.";
            return true;
        }
        catch (Exception ex)
        {
            RestartAudioService(stopOnly: false);
            message = $"Erro ao remover StrikeEQ Native APO: {ex.Message}";
            return false;
        }
    }

    private static bool TryCopyDll(string src, string dst)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                return true;
            File.Copy(src, dst, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void BackupFxProperties(string guid, string fxRegSubKey)
    {
        string backupPath = Path.Combine(NativeApoSharedMemory.SharedDirectory, $"backup_{guid.Trim('{', '}')}.json");
        if (File.Exists(backupPath)) return; // Keep original pre-StrikeEQ backup intact!

        var backup = new Dictionary<string, string?>();
        using var key = Registry.LocalMachine.OpenSubKey(fxRegSubKey, writable: false);
        if (key is not null)
        {
            backup[PKEY_FX_EndpointEffectClsid] = key.GetValue(PKEY_FX_EndpointEffectClsid) as string;
            if (key.GetValue(PKEY_EFX_ProcessingModes) is string[] modes)
                backup[PKEY_EFX_ProcessingModes] = string.Join("|", modes);
            else
                backup[PKEY_EFX_ProcessingModes] = null;
        }
        File.WriteAllText(backupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RestoreFxProperties(string guid, string fxRegSubKey)
    {
        string backupPath = Path.Combine(NativeApoSharedMemory.SharedDirectory, $"backup_{guid.Trim('{', '}')}.json");
        using var key = Registry.LocalMachine.OpenSubKey(fxRegSubKey, writable: true);
        if (key is null) return;

        if (File.Exists(backupPath))
        {
            var backup = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(backupPath));
            if (backup is not null)
            {
                if (backup.TryGetValue(PKEY_FX_EndpointEffectClsid, out string? origEfx) && !string.IsNullOrWhiteSpace(origEfx))
                    key.SetValue(PKEY_FX_EndpointEffectClsid, origEfx, RegistryValueKind.String);
                else
                    key.DeleteValue(PKEY_FX_EndpointEffectClsid, throwOnMissingValue: false);

                if (backup.TryGetValue(PKEY_EFX_ProcessingModes, out string? origModes) && !string.IsNullOrWhiteSpace(origModes))
                    key.SetValue(PKEY_EFX_ProcessingModes, origModes.Split('|'), RegistryValueKind.MultiString);
                else
                    key.DeleteValue(PKEY_EFX_ProcessingModes, throwOnMissingValue: false);
                return;
            }
        }

        key.DeleteValue(PKEY_FX_EndpointEffectClsid, throwOnMissingValue: false);
        key.DeleteValue(PKEY_EFX_ProcessingModes, throwOnMissingValue: false);
    }

    private static void GrantDirectoryPermissions(string dirPath)
    {
        var info = new DirectoryInfo(dirPath);
        var sec = info.GetAccessControl();
        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, flags, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.Modify, flags, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    private static void GrantFilePermissions(string filePath)
    {
        if (!File.Exists(filePath)) return;
        var info = new FileInfo(filePath);
        var sec = info.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    private static void RestartAudioService(bool stopOnly)
    {
        string cmd = stopOnly
            ? "/c net stop Audiosrv /y"
            : "/c net stop Audiosrv /y && net start Audiosrv";

        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        p?.WaitForExit(15000);
    }

    // ---- Win32 Registry Ownership & ACL Helpers (for TrustedInstaller-owned MMDevices keys) ----
    private const uint SE_REGISTRY_KEY = 4;
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    private const uint DACL_SECURITY_INFORMATION  = 0x00000004;
    private const uint TOKEN_ADJUST_PRIVILEGES    = 0x0020;
    private const uint TOKEN_QUERY                = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED       = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfoW(
        string pObjectName, uint ObjectType, uint SecurityInfo,
        byte[]? psidOwner, byte[]? psidGroup, IntPtr pDacl, IntPtr pSacl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            return;
        try
        {
            if (LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void TakeRegistryKeyOwnership(string machineKeyName)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] sidBytes = new byte[adminsSid.BinaryLength];
        adminsSid.GetBinaryForm(sidBytes, 0);

        SetNamedSecurityInfoW(machineKeyName, SE_REGISTRY_KEY, OWNER_SECURITY_INFORMATION, sidBytes, null, IntPtr.Zero, IntPtr.Zero);

        // Now grant Administrators FullControl via managed RegistrySecurity
        string subKeyPath = machineKeyName.StartsWith("MACHINE\\", StringComparison.OrdinalIgnoreCase)
            ? machineKeyName["MACHINE\\".Length..]
            : machineKeyName;

        using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
        if (key is not null)
        {
            var sec = key.GetAccessControl();
            sec.AddAccessRule(new RegistryAccessRule(adminsSid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            key.SetAccessControl(sec);
        }
    }
}
