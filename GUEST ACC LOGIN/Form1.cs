using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GuestAccountSetter
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        // Set button click
        private async void guna2ButtonSet_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(guna2TextBoxUID.Text) || string.IsNullOrWhiteSpace(guna2TextBoxPassword.Text))
            {
                UpdateStatus("Please enter both UID and Password.");
                return;
            }

            var result = MessageBox.Show(
                $"Set guest account to:\nUID: {guna2TextBoxUID.Text}\nPassword: {guna2TextBoxPassword.Text}",
                "Confirm",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            guna2ButtonSet.Enabled = false;
            try
            {
                await SetGuestAccountAsync(guna2TextBoxUID.Text.Trim(), guna2TextBoxPassword.Text.Trim());
            }
            catch (Exception ex)
            {
                UpdateStatus($"Unexpected error: {ex.Message}");
            }
            finally
            {
                guna2ButtonSet.Enabled = true;
            }
        }

        // Core method to set guest account using ADB shell (like the delete method)
        private async Task SetGuestAccountAsync(string uid, string password)
        {
            UpdateStatus("Setting guest account...");

            await Task.Run(async () =>
            {
                try
                {
                    // 1. Find BlueStacks process
                    Process[] processes = Process.GetProcessesByName("HD-Player");
                    if (processes.Length == 0)
                    {
                        UpdateStatus("Emulator not running (HD-Player not found).");
                        return;
                    }

                    using (Process process = processes[0])
                    {
                        string mainModulePath;
                        try
                        {
                            mainModulePath = Path.GetDirectoryName(process.MainModule.FileName);
                        }
                        catch (Exception ex)
                        {
                            UpdateStatus($"Cannot access process path: {ex.Message}");
                            return;
                        }

                        string adbPath = Path.Combine(mainModulePath, "HD-Adb.exe");
                        if (!File.Exists(adbPath))
                        {
                            UpdateStatus("ADB not found (HD-Adb.exe missing).");
                            return;
                        }

                        // 2. Fixed paths
                        string remoteDir = "/storage/emulated/0/com.garena.msdk";
                        string remoteFile = $"{remoteDir}/guest100067.dat";

                        // 3. Create directory if needed
                        var (mkdirSuccess, mkdirError) = await RunShellCommandAsync(adbPath, $"mkdir -p {remoteDir}");
                        if (!mkdirSuccess)
                        {
                            UpdateStatus($"Failed to create directory: {mkdirError}");
                            return;
                        }

                        // 4. Build JSON (compact, no extra spaces)
                        string json = $"{{\"guest_account_info\":{{\"com.garena.msdk.guest_password\":\"{password}\",\"com.garena.msdk.guest_uid\":\"{uid}\"}}}}";
                        // Escape any single quotes in the JSON for the shell command
                        string escapedJson = json.Replace("'", "'\\''");
                        string writeCommand = $"printf '%s' '{escapedJson}' > {remoteFile}";

                        var (writeSuccess, writeError) = await RunShellCommandAsync(adbPath, writeCommand);
                        if (!writeSuccess)
                        {
                            UpdateStatus($"Failed to write file: {writeError}");
                            return;
                        }

                        // 5. Set permissions (world-readable)
                        await RunShellCommandAsync(adbPath, $"chmod 644 {remoteFile}");

                        // 6. Restart game if package provided
                        string package = guna2TextBoxPackage.Text.Trim();
                        if (!string.IsNullOrEmpty(package))
                        {
                            UpdateStatus("Restarting game...");
                            bool restartOk = await RestartAppAsync(adbPath, package);
                            if (restartOk)
                                UpdateStatus("Guest account set and game restarted!");
                            else
                                UpdateStatus("Guest account set but game restart failed.");
                        }
                        else
                        {
                            UpdateStatus("Guest account set successfully! Restart the game manually.");
                        }
                    }

                    // Dispose remaining process objects
                    foreach (var p in processes) p?.Dispose();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error: {ex.Message}");
                }
            });
        }

        // Restart app: force-stop then launch
        private async Task<bool> RestartAppAsync(string adbPath, string package)
        {
            // Force-stop
            var (stopSuccess, stopError) = await RunShellCommandAsync(adbPath, $"am force-stop {package}");
            if (!stopSuccess)
            {
                UpdateStatus($"Force-stop failed: {stopError}");
                return false;
            }

            // Brief pause
            await Task.Delay(1000);

            // Launch using monkey
            var (launchSuccess, launchError) = await RunShellCommandAsync(adbPath, $"monkey -p {package} -c android.intent.category.LAUNCHER 1");
            if (!launchSuccess)
            {
                UpdateStatus($"Launch failed: {launchError}");
                return false;
            }

            return true;
        }

        // Run an ADB shell command
        private async Task<(bool success, string error)> RunShellCommandAsync(string adbPath, string shellCommand)
        {
            string arguments = $"shell \"{shellCommand.Replace("\"", "\\\"")}\"";
            return await RunAdbCommandAsync(adbPath, arguments);
        }

        // Generic ADB command runner
        private async Task<(bool success, string error)> RunAdbCommandAsync(string adbPath, string arguments)
        {
            var tcs = new TaskCompletionSource<(bool, string)>();

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            var output = new StringBuilder();
            var error = new StringBuilder();

            proc.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            proc.Exited += (s, e) =>
            {
                string err = error.ToString().Trim();
                // Success if exit code 0 and no error output
                bool success = proc.ExitCode == 0 && string.IsNullOrEmpty(err);
                tcs.SetResult((success, success ? "" : err));
                proc.Dispose();
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            return await tcs.Task;
        }

        // Thread-safe status update
        private void UpdateStatus(string text)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateStatus), text);
                return;
            }
            guna2HtmlLabelStatus.Text = text;
        }

        // Clear guest folder
        private async void guna2ButtonClear_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Delete the guest folder? This will force a new random guest on next launch.",
                "Confirm Clear",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            guna2ButtonClear.Enabled = false;
            try
            {
                await ClearGuestFolderAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Unexpected error: {ex.Message}");
            }
            finally
            {
                guna2ButtonClear.Enabled = true;
            }
        }

        private async Task ClearGuestFolderAsync()
        {
            UpdateStatus("Clearing guest folder...");

            await Task.Run(async () =>
            {
                try
                {
                    Process[] processes = Process.GetProcessesByName("HD-Player");
                    if (processes.Length == 0)
                    {
                        UpdateStatus("Emulator not running.");
                        return;
                    }

                    using (Process process = processes[0])
                    {
                        string mainModulePath = Path.GetDirectoryName(process.MainModule.FileName);
                        string adbPath = Path.Combine(mainModulePath, "HD-Adb.exe");
                        if (!File.Exists(adbPath))
                        {
                            UpdateStatus("ADB not found.");
                            return;
                        }

                        var (success, error) = await RunShellCommandAsync(adbPath, "rm -rf /storage/emulated/0/com.garena.msdk");
                        if (success)
                            UpdateStatus("Guest folder cleared.");
                        else
                            UpdateStatus($"Error: {error}");
                    }

                    foreach (var p in processes) p?.Dispose();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error: {ex.Message}");
                }
            });
        }
    }
}