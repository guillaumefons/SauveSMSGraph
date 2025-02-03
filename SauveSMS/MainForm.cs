using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Exceptions;

using Microsoft.Extensions.Logging;
using System.Threading.Tasks;


namespace SauveSMSGraphique
{
    public partial class MainForm : Form
    {
        static Dictionary<string, string> contactsCache = new Dictionary<string, string>();
        static readonly string adbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "ADB", "adb.exe");
        static readonly ILogger<MainForm> Logger = LoggerFactory.Create(builder => builder.AddConsole().AddFile("logs/app-{Date}.txt")).CreateLogger<MainForm>();

        public MainForm()
        {
            InitializeComponent();
        }

        private async void btnBackup_Click(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(adbPath))
                {
                    Logger.LogError("Le fichier ADB n'a pas été trouvé à l'emplacement : {AdbPath}", adbPath);
                    MessageBox.Show("Le fichier ADB n'a pas été trouvé.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!IsAdbServerRunning())
                {
                    Logger.LogInformation("Le serveur ADB n'est pas en cours d'exécution. Démarrage du serveur...");
                    StartAdbServer();
                }
                else
                {
                    Logger.LogInformation("Le serveur ADB est déjà en cours d'exécution.");
                }

                var adbClient = new AdbClient();
                var device = adbClient.GetDevices().FirstOrDefault();

                if (device == null)
                {
                    Logger.LogWarning("Aucun appareil Android connecté.");
                    MessageBox.Show("Aucun appareil Android connecté.", "Avertissement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Logger.LogInformation("Appareil détecté : {Model}", device.Model);

                string backupPath = CreateBackupFolder();
                LoadContacts(adbClient, device);

                var progress = new Progress<int>(value => progressBar.Value = value);
                await Task.Run(() => BackupSMS(adbClient, device, backupPath, progress));

                MessageBox.Show("Sauvegarde terminée avec succès.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (AdbException adbEx)
            {
                Logger.LogError(adbEx, "Erreur ADB : {Message}", adbEx.Message);
                MessageBox.Show($"Erreur ADB : {adbEx.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Une erreur inattendue s'est produite : {Message}", ex.Message);
                MessageBox.Show($"Une erreur inattendue s'est produite : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                progressBar.Value = 0;
            }
        }

        static bool IsAdbServerRunning()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = "devices",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return !output.Contains("* daemon not running");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Erreur lors de la vérification du statut du serveur ADB : {Message}", ex.Message);
                return false;
            }
        }

        static void StartAdbServer()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = "start-server",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
                Logger.LogInformation("Serveur ADB démarré avec succès.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Erreur lors du démarrage du serveur ADB : {Message}", ex.Message);
            }
        }

        static void LoadContacts(AdbClient adbClient, DeviceData device)
        {
            try
            {
                var receiver = new ConsoleOutputReceiver();
                adbClient.ExecuteRemoteCommand("content query --uri content://contacts/phones --projection display_name:number", device, receiver);
                string output = receiver.ToString();

                var contactEntries = output.Split(new[] { "Row:" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in contactEntries)
                {
                    var name = ExtractValue(entry, "display_name");
                    var number = ExtractValue(entry, "number");
                    if (!string.IsNullOrEmpty(number))
                    {
                        contactsCache[NormalizePhoneNumber(number)] = name;
                    }
                }
                Logger.LogInformation("Contacts chargés avec succès. Nombre de contacts : {Count}", contactsCache.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Erreur lors du chargement des contacts : {Message}", ex.Message);
            }
        }

        static string CreateBackupFolder()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string smsFolder = Path.Combine(userProfile, "Mes_SMS");

                if (!Directory.Exists(smsFolder))
                {
                    Directory.CreateDirectory(smsFolder);
                    Logger.LogInformation("Dossier créé : {Folder}", smsFolder);
                }

                return smsFolder;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Erreur lors de la création du dossier de sauvegarde : {Message}", ex.Message);
                throw;
            }
        }

        static void BackupSMS(AdbClient adbClient, DeviceData device, string backupPath, IProgress<int> progress)
        {
            string backupFile = Path.Combine(backupPath, $"sms_backup_{DateTime.Now:yyyyMMddHHmmss}.txt");

            try
            {
                var receiver = new ConsoleOutputReceiver();
                adbClient.ExecuteRemoteCommand("content query --uri content://sms", device, receiver);

                string output = receiver.ToString();
                var smsEntries = Regex.Split(output, @"Row: \d+\s+");
                var totalSMS = smsEntries.Length - 1;

                var formattedSMS = new StringBuilder();
                for (int i = 1; i < smsEntries.Length; i++)
                {
                    formattedSMS.Append(FormatSingleSMS(smsEntries[i]));
                    progress?.Report((i * 100) / totalSMS);
                }

                File.WriteAllText(backupFile, formattedSMS.ToString(), Encoding.UTF8);
                Logger.LogInformation("Sauvegarde réussie : {BackupFile}", backupFile);
            }
            catch (AdbException adbEx)
            {
                Logger.LogError(adbEx, "Erreur lors de l'exécution de la commande ADB : {Message}", adbEx.Message);
            }
            catch (IOException ioEx)
            {
                Logger.LogError(ioEx, "Erreur lors de l'écriture du fichier de sauvegarde : {Message}", ioEx.Message);
            }
        }

        static string FormatSingleSMS(string entry)
        {
            var date = ExtractValue(entry, "date");
            var address = ExtractValue(entry, "address");
            var type = ExtractValue(entry, "type");
            var body = ExtractFullBody(entry);

            if (!string.IsNullOrEmpty(date) && !string.IsNullOrEmpty(address))
            {
                var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(date)).LocalDateTime;
                var formattedDate = dateTime.ToString("yyyy-MM-dd");
                var formattedTime = dateTime.ToString("HH:mm:ss");
                var direction = type == "1" ? "De" : "À";
                var contactName = GetContactName(address);

                return $"Date: {formattedDate}\n" +
                       $"Heure: {formattedTime}\n" +
                       $"{direction}: {contactName} ({address})\n" +
                       $"Message: {body}\n\n";
            }

            return string.Empty;
        }

        static string ExtractValue(string input, string key)
        {
            var match = Regex.Match(input, $@"{key}=([^,\n]+)");
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        static string ExtractFullBody(string input)
        {
            var match = Regex.Match(input, @"body=(.+)(?=, sub_id=|$)", RegexOptions.Singleline);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim().Trim('"');
            }
            return string.Empty;
        }

        static string GetContactName(string phoneNumber)
        {
            string normalizedNumber = NormalizePhoneNumber(phoneNumber);
            return contactsCache.TryGetValue(normalizedNumber, out string name) ? name : phoneNumber;
        }

        static string NormalizePhoneNumber(string phoneNumber)
        {
            return Regex.Replace(phoneNumber, @"[^\d]", "");
        }

        private void InitializeComponent()
        {
            this.btnBackup = new System.Windows.Forms.Button();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.btnOpenBackupFolder = new System.Windows.Forms.Button();
            this.SuspendLayout();

            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(284, 105);
            this.Controls.Add(this.btnOpenBackupFolder);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.btnBackup);
            this.Name = "MainForm";
            this.Text = "Sauvegarde SMS Android";
            this.ResumeLayout(false);


            // 
            // btnBackup
            // 
            this.btnBackup.Location = new System.Drawing.Point(12, 12);
            this.btnBackup.Name = "btnBackup";
            this.btnBackup.Size = new System.Drawing.Size(260, 23);
            this.btnBackup.TabIndex = 0;
            this.btnBackup.Text = "Sauvegarder les SMS";
            this.btnBackup.UseVisualStyleBackColor = true;
            this.btnBackup.Click += new System.EventHandler(this.btnBackup_Click);
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(12, 41);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(260, 23);
            this.progressBar.TabIndex = 1;
            // 
            // btnOpenBackupFolder
            // 
            this.btnOpenBackupFolder.Location = new System.Drawing.Point(12, 70);
            this.btnOpenBackupFolder.Name = "btnOpenBackupFolder";
            this.btnOpenBackupFolder.Size = new System.Drawing.Size(260, 23);
            this.btnOpenBackupFolder.TabIndex = 2;
            this.btnOpenBackupFolder.Text = "Ouvrir le dossier de sauvegarde";
            this.btnOpenBackupFolder.UseVisualStyleBackColor = true;
            this.btnOpenBackupFolder.Click += new System.EventHandler(this.btnOpenBackupFolder_Click);
            
        }

        

        private void btnOpenBackupFolder_Click(object sender, EventArgs e)
        {
            try
            {
                string backupFolder = CreateBackupFolder();
                if (Directory.Exists(backupFolder))
                {
                    Process.Start("explorer.exe", backupFolder);
                }
                else
                {
                    MessageBox.Show("Le dossier de sauvegarde n'existe pas.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Erreur lors de l'ouverture du dossier de sauvegarde : {Message}", ex.Message);
                MessageBox.Show($"Erreur lors de l'ouverture du dossier de sauvegarde : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private System.Windows.Forms.Button btnBackup;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.Button btnOpenBackupFolder;
    }
}
