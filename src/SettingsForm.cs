using System;
using System.Collections.Generic;
using System.Drawing;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace TongYan
{
    public class SettingsForm : Form
    {
        private readonly AppConfig _config;
        private readonly Action _onSaved;

        private TextBox _txtBaseUrl;
        private TextBox _txtApiKey;
        private Button _btnToggleKey;
        private ComboBox _cmbModel;
        private Button _btnFetchModels;
        private CheckBox _chkDisableThinking;
        private Label _lblFetchStatus;

        private ComboBox _cmbPrompts;
        private Button _btnNewPrompt;
        private Button _btnRenamePrompt;
        private Button _btnDeletePrompt;
        private Button _btnResetPrompt;

        private TextBox _txtPromptName;
        private Button _btnConfirmName;
        private Button _btnCancelName;
        private bool _isNamingNew = false;

        private TextBox _txtPromptContent;

        private Button _btnKeybind;
        private CheckBox _chkAutoStart;

        private bool _isRecording = false;
        private bool _suppressPromptEvents = false;
        private string _currentHotkey;
        private readonly List<string> _workingModels = new List<string>();
        private readonly List<PromptPreset> _workingPrompts = new List<PromptPreset>();
        private string _activePromptId;

        public SettingsForm(AppConfig config, Action onSaved)
        {
            _config = config;
            _onSaved = onSaved;
            _currentHotkey = string.IsNullOrEmpty(config.hotkey) ? "Alt+D" : config.hotkey;

            if (config.cached_models != null)
            {
                for (int i = 0; i < config.cached_models.Count; i++)
                {
                    string m = config.cached_models[i];
                    if (!string.IsNullOrEmpty(m))
                    {
                        _workingModels.Add(m);
                    }
                }
            }

            if (config.prompts != null && config.prompts.Count > 0)
            {
                for (int i = 0; i < config.prompts.Count; i++)
                {
                    PromptPreset p = config.prompts[i];
                    if (p != null)
                    {
                        _workingPrompts.Add(new PromptPreset
                        {
                            id = p.id,
                            name = p.name,
                            prompt = p.prompt
                        });
                    }
                }
            }

            if (_workingPrompts.Count == 0)
            {
                _workingPrompts.AddRange(AppConfig.CreateDefaultPrompts());
            }

            _activePromptId = config.active_prompt_id;
            EnsureActivePromptValid();

            InitializeUI();
        }

        private void EnsureActivePromptValid()
        {
            bool found = false;
            for (int i = 0; i < _workingPrompts.Count; i++)
            {
                if (_workingPrompts[i].id == _activePromptId)
                {
                    found = true;
                    break;
                }
            }
            if (!found && _workingPrompts.Count > 0)
            {
                _activePromptId = _workingPrompts[0].id;
            }
        }

        private PromptPreset GetSelectedWorkingPrompt()
        {
            for (int i = 0; i < _workingPrompts.Count; i++)
            {
                if (_workingPrompts[i].id == _activePromptId)
                {
                    return _workingPrompts[i];
                }
            }
            return _workingPrompts.Count > 0 ? _workingPrompts[0] : null;
        }

        private void InitializeUI()
        {
            Text = "通言设置";
            ClientSize = new Size(464, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            if (Program.AppIcon != null)
            {
                try { Icon = Program.AppIcon; } catch { }
            }

            // 1. 接口地址
            Label lblBaseUrl = new Label
            {
                Text = "接口地址:",
                Location = new Point(24, 16),
                AutoSize = true,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblBaseUrl);

            _txtBaseUrl = new TextBox
            {
                Text = _config.base_url ?? "",
                Location = new Point(24, 38),
                Width = 416,
                Font = new Font("Consolas", 10f)
            };
            _txtBaseUrl.Enter += OnInputControlEnter;
            Controls.Add(_txtBaseUrl);

            // 2. API Key
            Label lblApiKey = new Label
            {
                Text = "API Key:",
                Location = new Point(24, 72),
                AutoSize = true,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblApiKey);

            _txtApiKey = new TextBox
            {
                Text = _config.api_key ?? "",
                Location = new Point(24, 94),
                Width = 352,
                PasswordChar = '*',
                Font = new Font("Consolas", 10f)
            };
            _txtApiKey.Enter += OnInputControlEnter;
            Controls.Add(_txtApiKey);

            _btnToggleKey = CreateFlatButton("显示", new Point(382, 93), new Size(58, 25));
            _btnToggleKey.Click += (s, e) =>
            {
                bool hidden = _txtApiKey.PasswordChar == '*';
                _txtApiKey.PasswordChar = hidden ? '\0' : '*';
                _btnToggleKey.Text = hidden ? "隐藏" : "显示";
            };
            Controls.Add(_btnToggleKey);

            // 3. 模型名称与获取模型按钮
            Label lblModel = new Label
            {
                Text = "模型名称:",
                Location = new Point(24, 128),
                AutoSize = true,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblModel);

            _lblFetchStatus = new Label
            {
                Text = "",
                Location = new Point(92, 130),
                Size = new Size(348, 18),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            Controls.Add(_lblFetchStatus);

            _cmbModel = new ComboBox
            {
                Location = new Point(24, 150),
                Width = 324,
                DropDownStyle = ComboBoxStyle.DropDown,
                Font = new Font("Consolas", 10f)
            };
            _cmbModel.Enter += OnInputControlEnter;
            RefreshModelItems(_config.model);
            Controls.Add(_cmbModel);

            _btnFetchModels = CreateFlatButton("获取模型", new Point(354, 149), new Size(86, 26));
            _btnFetchModels.Click += (s, e) => FetchModels();
            Controls.Add(_btnFetchModels);

            // 4. 提示词设定区域
            Label lblPrompt = new Label
            {
                Text = "提示词设定:",
                Location = new Point(24, 186),
                AutoSize = true,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblPrompt);

            _cmbPrompts = new ComboBox
            {
                Location = new Point(24, 208),
                Width = 176,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            _cmbPrompts.Enter += OnInputControlEnter;
            _cmbPrompts.SelectedIndexChanged += OnPromptSelectionChanged;
            Controls.Add(_cmbPrompts);

            _btnNewPrompt = CreateFlatButton("新建", new Point(206, 207), new Size(48, 26));
            _btnNewPrompt.Click += (s, e) => BeginInlinePromptNaming(true);
            Controls.Add(_btnNewPrompt);

            _btnRenamePrompt = CreateFlatButton("重命名", new Point(258, 207), new Size(58, 26));
            _btnRenamePrompt.Click += (s, e) => BeginInlinePromptNaming(false);
            Controls.Add(_btnRenamePrompt);

            _btnDeletePrompt = CreateFlatButton("删除", new Point(320, 207), new Size(48, 26));
            _btnDeletePrompt.Click += (s, e) => DeleteSelectedPrompt();
            Controls.Add(_btnDeletePrompt);

            _btnResetPrompt = CreateFlatButton("恢复默认", new Point(372, 207), new Size(68, 26));
            _btnResetPrompt.Click += (s, e) => ResetDefaultPrompt();
            Controls.Add(_btnResetPrompt);

            // 内联提示词命名控件组
            _txtPromptName = new TextBox
            {
                Location = new Point(24, 208),
                Width = 300,
                Visible = false,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            _txtPromptName.Enter += OnInputControlEnter;
            _txtPromptName.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    CommitInlinePromptNaming();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    CancelInlinePromptNaming();
                }
            };
            Controls.Add(_txtPromptName);

            _btnConfirmName = CreateFlatButton("确定", new Point(330, 207), new Size(52, 26));
            _btnConfirmName.Visible = false;
            _btnConfirmName.Click += (s, e) => CommitInlinePromptNaming();
            Controls.Add(_btnConfirmName);

            _btnCancelName = CreateFlatButton("取消", new Point(386, 207), new Size(54, 26));
            _btnCancelName.Visible = false;
            _btnCancelName.Click += (s, e) => CancelInlinePromptNaming();
            Controls.Add(_btnCancelName);

            _txtPromptContent = new TextBox
            {
                Location = new Point(24, 240),
                Size = new Size(416, 115),
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            _txtPromptContent.Enter += OnInputControlEnter;
            _txtPromptContent.TextChanged += OnPromptContentChanged;
            Controls.Add(_txtPromptContent);

            RefreshPromptComboBox();

            // 5. 快捷键设置区域
            Label lblHotkey = new Label
            {
                Text = "全局快捷键:",
                Location = new Point(24, 368),
                AutoSize = true,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblHotkey);

            _btnKeybind = new Button
            {
                Location = new Point(24, 390),
                Size = new Size(416, 38),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnKeybind.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            _btnKeybind.FlatAppearance.BorderSize = 1;
            _btnKeybind.Click += (s, e) => StartRecording();
            Controls.Add(_btnKeybind);

            // 6. 勾选选项行（关闭思考模式 + 开机自启动 并排）
            _chkDisableThinking = new CheckBox
            {
                Text = "关闭思考模式",
                Location = new Point(24, 442),
                AutoSize = true,
                Checked = _config.disable_thinking,
                ForeColor = Color.FromArgb(40, 40, 40),
                Cursor = Cursors.Hand
            };
            Controls.Add(_chkDisableThinking);

            _chkAutoStart = new CheckBox
            {
                Text = "开机自启动",
                Location = new Point(156, 442),
                AutoSize = true,
                Checked = _config.auto_start,
                ForeColor = Color.FromArgb(40, 40, 40),
                Cursor = Cursors.Hand
            };
            Controls.Add(_chkAutoStart);

            // 7. 底部保存与取消按钮行
            Button btnSave = new Button
            {
                Text = "保存",
                Location = new Point(256, 472),
                Size = new Size(88, 32),
                BackColor = Color.FromArgb(24, 120, 230),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) => SaveAndClose();
            Controls.Add(btnSave);

            Button btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(352, 472),
                Size = new Size(88, 32),
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
            btnCancel.Click += (s, e) => Close();
            Controls.Add(btnCancel);

            UpdateKeybindButton();
        }

        private Button CreateFlatButton(string text, Point location, Size size)
        {
            Button btn = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                BackColor = Color.FromArgb(246, 247, 249),
                ForeColor = Color.FromArgb(50, 50, 50),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(205, 205, 205);
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        private void OnInputControlEnter(object sender, EventArgs e)
        {
            if (_isRecording)
            {
                StopRecording(null);
            }
        }

        private void RefreshModelItems(string selectedModel)
        {
            _cmbModel.BeginUpdate();
            _cmbModel.Items.Clear();
            for (int i = 0; i < _workingModels.Count; i++)
            {
                _cmbModel.Items.Add(_workingModels[i]);
            }
            _cmbModel.EndUpdate();

            if (!string.IsNullOrEmpty(selectedModel))
            {
                _cmbModel.Text = selectedModel;
            }
            else if (_workingModels.Count > 0)
            {
                _cmbModel.Text = _workingModels[0];
            }
            else
            {
                _cmbModel.Text = "";
            }
        }

        private void FetchModels()
        {
            if (_isRecording) StopRecording(null);

            string baseUrl = _txtBaseUrl.Text.Trim();
            string apiKey = _txtApiKey.Text.Trim();

            if (string.IsNullOrEmpty(baseUrl))
            {
                _lblFetchStatus.ForeColor = Color.FromArgb(190, 40, 40);
                _lblFetchStatus.Text = "请先填写接口地址";
                return;
            }

            _btnFetchModels.Enabled = false;
            _btnFetchModels.Text = "获取中...";
            _lblFetchStatus.Text = "";

            AiClient.FetchModelsAsync(
                baseUrl,
                apiKey,
                models =>
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            _btnFetchModels.Enabled = true;
                            _btnFetchModels.Text = "获取模型";

                            string previousModel = _cmbModel.Text.Trim();
                            _workingModels.Clear();
                            _workingModels.AddRange(models);

                            string targetModel = previousModel;
                            if (string.IsNullOrEmpty(targetModel) || !_workingModels.Contains(targetModel))
                            {
                                targetModel = _workingModels.Count > 0 ? _workingModels[0] : previousModel;
                            }

                            RefreshModelItems(targetModel);
                            _lblFetchStatus.ForeColor = Color.FromArgb(30, 140, 60);
                            _lblFetchStatus.Text = "已获取 " + models.Count + " 个模型";

                            try { _cmbModel.DroppedDown = true; } catch { }
                        }));
                    }
                    catch { }
                },
                err =>
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            _btnFetchModels.Enabled = true;
                            _btnFetchModels.Text = "获取模型";
                            _lblFetchStatus.ForeColor = Color.FromArgb(190, 40, 40);
                            _lblFetchStatus.Text = err;
                        }));
                    }
                    catch { }
                }
            );
        }

        private void RefreshPromptComboBox()
        {
            _suppressPromptEvents = true;
            _cmbPrompts.BeginUpdate();
            _cmbPrompts.Items.Clear();

            int selectedIdx = 0;
            for (int i = 0; i < _workingPrompts.Count; i++)
            {
                PromptPreset p = _workingPrompts[i];
                _cmbPrompts.Items.Add(p.name);
                if (p.id == _activePromptId)
                {
                    selectedIdx = i;
                }
            }
            _cmbPrompts.EndUpdate();

            if (_cmbPrompts.Items.Count > 0)
            {
                _cmbPrompts.SelectedIndex = selectedIdx;
                _activePromptId = _workingPrompts[selectedIdx].id;
                _txtPromptContent.Text = (_workingPrompts[selectedIdx].prompt ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            }

            _btnDeletePrompt.Enabled = _workingPrompts.Count > 1;
            _suppressPromptEvents = false;
        }

        private void OnPromptSelectionChanged(object sender, EventArgs e)
        {
            if (_suppressPromptEvents) return;
            int idx = _cmbPrompts.SelectedIndex;
            if (idx >= 0 && idx < _workingPrompts.Count)
            {
                _activePromptId = _workingPrompts[idx].id;
                _suppressPromptEvents = true;
                _txtPromptContent.Text = (_workingPrompts[idx].prompt ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                _suppressPromptEvents = false;
            }
        }

        private void OnPromptContentChanged(object sender, EventArgs e)
        {
            if (_suppressPromptEvents) return;
            PromptPreset current = GetSelectedWorkingPrompt();
            if (current != null)
            {
                current.prompt = _txtPromptContent.Text.Replace("\r\n", "\n");
            }
        }

        private void BeginInlinePromptNaming(bool isNew)
        {
            if (_isRecording) StopRecording(null);
            _isNamingNew = isNew;

            PromptPreset current = GetSelectedWorkingPrompt();
            _txtPromptName.Text = isNew ? "自定义提示词" : (current != null ? current.name : "");

            _cmbPrompts.Visible = false;
            _btnNewPrompt.Visible = false;
            _btnRenamePrompt.Visible = false;
            _btnDeletePrompt.Visible = false;
            _btnResetPrompt.Visible = false;

            _txtPromptName.Visible = true;
            _btnConfirmName.Visible = true;
            _btnCancelName.Visible = true;

            _txtPromptName.Focus();
            _txtPromptName.SelectAll();
        }

        private void CommitInlinePromptNaming()
        {
            string name = _txtPromptName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                CancelInlinePromptNaming();
                return;
            }

            if (_isNamingNew)
            {
                string newId = "prompt_" + DateTime.Now.Ticks.ToString();
                PromptPreset created = new PromptPreset
                {
                    id = newId,
                    name = name,
                    prompt = ""
                };
                _workingPrompts.Add(created);
                _activePromptId = newId;
            }
            else
            {
                PromptPreset current = GetSelectedWorkingPrompt();
                if (current != null)
                {
                    current.name = name;
                }
            }

            EndInlinePromptNaming();
            RefreshPromptComboBox();

            if (_isNamingNew)
            {
                _txtPromptContent.Focus();
            }
        }

        private void CancelInlinePromptNaming()
        {
            EndInlinePromptNaming();
        }

        private void EndInlinePromptNaming()
        {
            _txtPromptName.Visible = false;
            _btnConfirmName.Visible = false;
            _btnCancelName.Visible = false;

            _cmbPrompts.Visible = true;
            _btnNewPrompt.Visible = true;
            _btnRenamePrompt.Visible = true;
            _btnDeletePrompt.Visible = true;
            _btnResetPrompt.Visible = true;
        }

        private void DeleteSelectedPrompt()
        {
            if (_workingPrompts.Count <= 1) return;

            for (int i = 0; i < _workingPrompts.Count; i++)
            {
                if (_workingPrompts[i].id == _activePromptId)
                {
                    _workingPrompts.RemoveAt(i);
                    int nextIdx = Math.Min(i, _workingPrompts.Count - 1);
                    _activePromptId = _workingPrompts[nextIdx].id;
                    break;
                }
            }

            RefreshPromptComboBox();
        }

        private void ResetDefaultPrompt()
        {
            PromptPreset current = GetSelectedWorkingPrompt();
            if (current != null)
            {
                current.prompt = AppConfig.DefaultSystemPrompt;
                _suppressPromptEvents = true;
                _txtPromptContent.Text = AppConfig.DefaultSystemPrompt.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                _suppressPromptEvents = false;
            }
        }

        private void StartRecording()
        {
            _isRecording = true;
            _btnKeybind.BackColor = Color.FromArgb(235, 245, 255);
            _btnKeybind.ForeColor = Color.FromArgb(20, 110, 230);
            _btnKeybind.FlatAppearance.BorderColor = Color.FromArgb(20, 110, 230);
            _btnKeybind.FlatAppearance.BorderSize = 2;
            _btnKeybind.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            _btnKeybind.Text = "请按下新快捷键，按 ESC 取消";
        }

        private void StopRecording(string newKey = null)
        {
            _isRecording = false;
            if (!string.IsNullOrEmpty(newKey))
            {
                _currentHotkey = newKey;
                try { SystemSounds.Asterisk.Play(); } catch { }
            }

            UpdateKeybindButton();
        }

        private void UpdateKeybindButton()
        {
            _btnKeybind.BackColor = Color.FromArgb(248, 249, 250);
            _btnKeybind.ForeColor = Color.FromArgb(30, 30, 30);
            _btnKeybind.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            _btnKeybind.FlatAppearance.BorderSize = 1;
            _btnKeybind.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            _btnKeybind.Text = _currentHotkey.Replace(" ", "").Replace("+", " + ");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_isRecording)
            {
                Keys modifiers = keyData & Keys.Modifiers;
                Keys key = keyData & Keys.KeyCode;

                if (key == Keys.Escape)
                {
                    StopRecording(null);
                    return true;
                }

                if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu)
                {
                    StringBuilder holding = new StringBuilder();
                    if ((modifiers & Keys.Control) == Keys.Control) holding.Append("Ctrl + ");
                    if ((modifiers & Keys.Alt) == Keys.Alt) holding.Append("Alt + ");
                    if ((modifiers & Keys.Shift) == Keys.Shift) holding.Append("Shift + ");
                    _btnKeybind.Text = holding.ToString() + "...";
                    return true;
                }

                StringBuilder sb = new StringBuilder();
                if ((modifiers & Keys.Control) == Keys.Control) sb.Append("Ctrl+");
                if ((modifiers & Keys.Alt) == Keys.Alt) sb.Append("Alt+");
                if ((modifiers & Keys.Shift) == Keys.Shift) sb.Append("Shift+");
                sb.Append(key.ToString());

                string recorded = sb.ToString();
                StopRecording(recorded);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SaveAndClose()
        {
            if (_txtPromptName.Visible)
            {
                CommitInlinePromptNaming();
            }

            _config.base_url = _txtBaseUrl.Text.Trim();
            _config.api_key = _txtApiKey.Text.Trim();
            _config.model = _cmbModel.Text.Trim();
            _config.disable_thinking = _chkDisableThinking.Checked;

            _config.cached_models = new List<string>(_workingModels);
            if (!string.IsNullOrEmpty(_config.model) && !_config.cached_models.Contains(_config.model))
            {
                _config.cached_models.Insert(0, _config.model);
            }

            _config.prompts = new List<PromptPreset>(_workingPrompts);
            _config.active_prompt_id = _activePromptId;
            _config.hotkey = _currentHotkey.Trim();
            _config.auto_start = _chkAutoStart != null && _chkAutoStart.Checked;
            _config.Save();

            if (_onSaved != null)
            {
                _onSaved();
            }

            Close();
        }
    }
}
