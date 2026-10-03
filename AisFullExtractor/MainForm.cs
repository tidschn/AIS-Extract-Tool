using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AisFullExtractor
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                Filter = "AIS JSON file (*.json)|*.json|All files (*.*)|*.*",
                Title = "Select the encrypted AIS JSON file"
            })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                    txtFilePath.Text = ofd.FileName;
            }
        }

        private void chkShowPassword_CheckedChanged(object sender, EventArgs e)
        {
            txtPassword.PasswordChar = chkShowPassword.Checked ? '\0' : '*';
        }

        private async void btnProcess_Click(object sender, EventArgs e)
        {
            string filePath = txtFilePath.Text.Trim();
            string password = txtPassword.Text;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show(this, "Please select a valid encrypted AIS JSON file.", "Missing file",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (password.Length != 18)
            {
                MessageBox.Show(this,
                    "Password must be exactly 18 characters: 10-character PAN followed by an 8-digit date of birth (ddmmyyyy).",
                    "Invalid password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string pan = password.Substring(0, 10);
            string dob = password.Substring(10, 8);

            using (var sfd = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = Path.GetFileNameWithoutExtension(filePath) + "_AIS_Full.xlsx",
                Title = "Save Excel report as"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                string outputPath = sfd.FileName;
                SetBusy(true, "Decrypting...");

                try
                {
                    await Task.Run(() =>
                    {
                        string json = AisDecryptor.DecryptFile(filePath, pan, dob);
                        SetStatusThreadSafe("Extracting all elements to Excel...");
                        AisFullExporter.Export(json, outputPath);
                    });

                    lblStatus.Text = "Done.";
                    var openIt = MessageBox.Show(this, "Excel report generated successfully.\n\nOpen it now?",
                        "Success", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (openIt == DialogResult.Yes)
                        System.Diagnostics.Process.Start(outputPath);
                }
                catch (AisDecryptor.AisDecryptionException ex)
                {
                    lblStatus.Text = "Failed.";
                    MessageBox.Show(this, ex.Message, "Decryption failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception ex)
                {
                    lblStatus.Text = "Failed.";
                    MessageBox.Show(this, "Unexpected error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    SetBusy(false, lblStatus.Text);
                }
            }
        }

        private void SetStatusThreadSafe(string text)
        {
            if (InvokeRequired) Invoke(new Action(() => lblStatus.Text = text));
            else lblStatus.Text = text;
        }

        private void SetBusy(bool busy, string status)
        {
            btnProcess.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            lblStatus.Text = status;
        }
    }
}
