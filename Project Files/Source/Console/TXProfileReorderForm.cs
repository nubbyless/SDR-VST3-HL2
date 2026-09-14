using System;
using System.Data;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public sealed class TXProfileReorderForm : Form
    {
        private readonly ListBox _listBox;
        private readonly Button _upButton;
        private readonly Button _downButton;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly DataTable _table;

        public TXProfileReorderForm(DataTable txProfileTable)
        {
            _table = txProfileTable;

            Text = "Reorder TX Profiles";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(250, 340);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var profilesLabel = new Label
            {
                Text = "TX Profiles (top = first):",
                Location = new Point(10, 10),
                AutoSize = true
            };
            Controls.Add(profilesLabel);

            _listBox = new ListBox
            {
                Location = new Point(10, 30),
                Size = new Size(185, 270),
                SelectionMode = SelectionMode.One
            };
            Controls.Add(_listBox);

            _upButton = new Button
            {
                Text = "Up",
                Location = new Point(205, 30),
                Size = new Size(35, 70),
                Enabled = false
            };
            _upButton.Click += UpButton_Click;
            Controls.Add(_upButton);

            _downButton = new Button
            {
                Text = "Down",
                Location = new Point(205, 110),
                Size = new Size(35, 70),
                Enabled = false
            };
            _downButton.Click += DownButton_Click;
            Controls.Add(_downButton);

            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(10, 308),
                Size = new Size(75, 25),
                DialogResult = DialogResult.OK
            };
            _okButton.Click += OKButton_Click;
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(165, 308),
                Size = new Size(75, 25),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            _listBox.SelectedIndexChanged += ListBox_SelectedIndexChanged;

            LoadProfiles();
        }

        private void LoadProfiles()
        {
            _listBox.Items.Clear();

            DataRow[] rows = _table.Select("", "Ordinal ASC");
            foreach (DataRow row in rows)
            {
                if (!row.IsNull("Name"))
                    _listBox.Items.Add(row["Name"].ToString());
            }

            if (_listBox.Items.Count > 0)
                _listBox.SelectedIndex = 0;
        }

        private void ListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = _listBox.SelectedIndex;
            _upButton.Enabled = idx > 0;
            _downButton.Enabled = idx >= 0 && idx < _listBox.Items.Count - 1;
        }

        private void UpButton_Click(object sender, EventArgs e)
        {
            int idx = _listBox.SelectedIndex;
            if (idx <= 0) return;

            object item = _listBox.Items[idx];
            _listBox.Items[idx] = _listBox.Items[idx - 1];
            _listBox.Items[idx - 1] = item;
            _listBox.SelectedIndex = idx - 1;
        }

        private void DownButton_Click(object sender, EventArgs e)
        {
            int idx = _listBox.SelectedIndex;
            if (idx < 0 || idx >= _listBox.Items.Count - 1) return;

            object item = _listBox.Items[idx];
            _listBox.Items[idx] = _listBox.Items[idx + 1];
            _listBox.Items[idx + 1] = item;
            _listBox.SelectedIndex = idx + 1;
        }

        private void OKButton_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < _listBox.Items.Count; i++)
            {
                string name = _listBox.Items[i].ToString();
                DataRow[] rows = _table.Select("Name = '" + name.Replace("'", "''") + "'");
                if (rows.Length > 0)
                    rows[0]["Ordinal"] = i;
            }
        }
    }
}
