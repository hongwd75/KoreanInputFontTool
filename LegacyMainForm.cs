namespace KoreanInputFontTool;

public sealed class LegacyMainForm : Form
{
    private readonly TextBox daocRootTextBox = new();
    private readonly ComboBox keyboardLayoutComboBox = new();
    private readonly ComboBox legacyGlyphModeComboBox = new();
    private readonly Label statusLabel = new();
    private readonly Button legacyHookButton = new();
    private readonly Button translationSettingsButton = new();
    private readonly LegacyKeyboardHook legacyHook = new();
    private readonly LegacyRenderHookService renderHook = new();
    private readonly ChatTranslationService chatTranslation = new();
    private readonly System.Windows.Forms.Timer renderHookTimer = new() { Interval = 2_000 };

    public LegacyMainForm()
    {
        Text = "KoreanInputFontTool v18.24 x64 - 한글 입력";
        var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (executableIcon is not null)
            Icon = executableIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(600, 405);
        Size = new Size(600, 405);

        FormClosed += (_, _) =>
        {
            renderHookTimer.Stop();
            renderHookTimer.Dispose();
            chatTranslation.Dispose();
            legacyHook.Dispose();
        };
        FormClosing += (_, _) => SaveDaocRootPath(showWarning: false);
        Shown += (_, _) => OnFormShown();
        legacyHook.HangulModeChanged += OnHangulModeChanged;
        legacyHook.InputDiagnosticChanged += OnInputDiagnosticChanged;
        chatTranslation.StatusChanged += OnTranslationStatusChanged;
        renderHookTimer.Tick += (_, _) => EnsurePrecomposedRenderHook();

        BuildLayout();
        ApplySelectedKeyboardLayout();
        ApplySelectedGlyphMode();
        UpdateLegacyHookButton();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);

        var titlePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4)
        };
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        titlePanel.Controls.Add(new Label
        {
            Text = "한글 입력 및 폰트 도구",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        translationSettingsButton.Text = "⚙ 설정";
        translationSettingsButton.Dock = DockStyle.Fill;
        translationSettingsButton.Click += OpenSettings;
        titlePanel.Controls.Add(translationSettingsButton, 1, 0);
        root.Controls.Add(titlePanel, 0, 0);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(settings, 0, 1);

        daocRootTextBox.Text = RegistrySettings.LoadDaocRootPath();
        daocRootTextBox.Dock = DockStyle.Fill;
        daocRootTextBox.Validated += (_, _) => SaveDaocRootPath(showWarning: true);
        AddLabel(settings, "DAoC 폴더", 0, 0);
        settings.Controls.Add(daocRootTextBox, 1, 0);
        var browseRoot = new Button { Text = "찾기...", Dock = DockStyle.Fill };
        browseRoot.Click += BrowseRoot;
        settings.Controls.Add(browseRoot, 2, 0);

        keyboardLayoutComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        keyboardLayoutComboBox.Items.AddRange([
            "두벌식 조합",
            "세벌식 최종 조합"
        ]);
        keyboardLayoutComboBox.SelectedIndex = RegistrySettings.LoadKeyboardLayout() == HangulKeyboardLayout.SebeolsikFinal
            ? 1
            : 0;
        keyboardLayoutComboBox.SelectedIndexChanged += (_, _) => OnKeyboardLayoutChanged();
        keyboardLayoutComboBox.Dock = DockStyle.Fill;
        AddLabel(settings, "입력 모드", 0, 1);
        settings.Controls.Add(keyboardLayoutComboBox, 1, 1);
        settings.SetColumnSpan(keyboardLayoutComboBox, 2);

        legacyGlyphModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        legacyGlyphModeComboBox.Items.AddRange([
            "KDAOC 모드",
            "확장된 한글",
            "완성형 출력"
        ]);
        legacyGlyphModeComboBox.SelectedIndex = (int)RegistrySettings.LoadHangulDisplayMode();
        legacyGlyphModeComboBox.SelectedIndexChanged += (_, _) => OnLegacyGlyphModeChanged();
        legacyGlyphModeComboBox.Dock = DockStyle.Fill;
        AddLabel(settings, "한글출력", 0, 2);
        settings.Controls.Add(legacyGlyphModeComboBox, 1, 2);
        settings.SetColumnSpan(legacyGlyphModeComboBox, 2);

        var actions = new GroupBox
        {
            Text = "한글 폰트 및 입력 상태",
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 12, 3, 6)
        };
        root.Controls.Add(actions, 0, 2);
        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 4
        };
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        actionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        actionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        actionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.Controls.Add(actionPanel);

        var applyButton = new Button { Text = "선택 모드 채팅 폰트 적용", Dock = DockStyle.Fill };
        applyButton.Click += ApplyFont;
        actionPanel.Controls.Add(applyButton, 0, 0);

        var restoreButton = new Button { Text = "폰트 원복", Dock = DockStyle.Fill };
        restoreButton.Click += RestoreFont;
        actionPanel.Controls.Add(restoreButton, 0, 1);

        legacyHookButton.Dock = DockStyle.Fill;
        legacyHookButton.Click += ToggleLegacyHook;
        actionPanel.Controls.Add(legacyHookButton, 0, 2);

        statusLabel.AutoSize = false;
        statusLabel.AutoEllipsis = true;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Text = "앱이 열리면 한글 입력을 자동으로 시작합니다.";
        root.Controls.Add(statusLabel, 0, 3);
    }

    private void OpenSettings(object? sender, EventArgs e)
    {
        using var dialog = new TranslationSettingsForm(RegistrySettings.LoadTranslationOptions());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            RegistrySettings.SaveTranslationOptions(dialog.Options);
            chatTranslation.Update(dialog.Options, renderHook.ActiveProcessIds);
            statusLabel.Text = dialog.Options.Enabled
                ? "게임 채팅 자동 번역 설정을 저장했습니다."
                : "채팅 자동 번역을 사용하지 않도록 저장했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "번역 설정 저장 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplyFont(object? sender, EventArgs e)
    {
        ApplySelectedFont(automatic: false);
    }

    private void ApplySelectedFont(bool automatic)
    {
        try
        {
            var path = ChatFontInstaller.Apply(daocRootTextBox.Text.Trim(), SelectedGlyphMode);
            var action = automatic ? "자동 적용 완료" : "적용 완료";
            statusLabel.Text = $"{SelectedGlyphModeName} 채팅 폰트 {action}: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "폰트 적용 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreFont(object? sender, EventArgs e)
    {
        statusLabel.Text = ChatFontInstaller.Restore(daocRootTextBox.Text.Trim())
            ? "Atlantis/custom 채팅 폰트를 원래 상태로 복원했습니다."
            : "원본 백업을 찾지 못했습니다.";
    }

    private void ToggleLegacyHook(object? sender, EventArgs e)
    {
        if (legacyHook.Enabled)
        {
            legacyHook.Enabled = false;
            legacyHook.Stop();
            UpdateLegacyHookButton();
            statusLabel.Text = "한글 입력 상태: 중지됨";
            return;
        }

        StartLegacyHook();
    }

    private void OnFormShown()
    {
        StartLegacyHook();
        ApplySelectedFont(automatic: true);
        UpdateRenderHookTimer();
        UpdateTranslationAvailability();
        EnsurePrecomposedRenderHook();
    }

    private void StartLegacyHook()
    {
        if (legacyHook.Enabled)
            return;

        try
        {
            ApplySelectedKeyboardLayout();
            legacyHook.Start();
            legacyHook.Enabled = true;
            UpdateLegacyHookButton();
            statusLabel.Text = $"한글 입력 자동 실행 중 / {SelectedLayoutName} / {SelectedGlyphModeName} / 영문 입력 중";
        }
        catch (Exception ex)
        {
            UpdateLegacyHookButton();
            MessageBox.Show(this, ex.Message, "키보드 훅 시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnKeyboardLayoutChanged()
    {
        ApplySelectedKeyboardLayout();
        try
        {
            RegistrySettings.SaveKeyboardLayout(SelectedKeyboardLayout);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "입력 모드 설정 저장 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        if (legacyHook.Enabled)
            statusLabel.Text = $"입력 모드 변경: {SelectedLayoutName} / {SelectedGlyphModeName} / {(legacyHook.HangulMode ? "한글" : "영문")} 입력 중";
    }

    private void OnLegacyGlyphModeChanged()
    {
        ApplySelectedGlyphMode();
        Exception? saveError = null;
        try
        {
            RegistrySettings.SaveHangulDisplayMode(SelectedGlyphMode);
        }
        catch (Exception ex)
        {
            saveError = ex;
        }

        ApplySelectedFont(automatic: true);
        UpdateRenderHookTimer();
        EnsurePrecomposedRenderHook();
        if (saveError is not null)
        {
            MessageBox.Show(
                this,
                saveError.Message,
                "한글출력 설정 저장 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplySelectedKeyboardLayout()
    {
        legacyHook.KeyboardLayout = SelectedKeyboardLayout;
    }

    private void ApplySelectedGlyphMode()
    {
        legacyHook.GlyphMode = SelectedGlyphMode;
        UpdateTranslationAvailability();
    }

    private string SelectedLayoutName => keyboardLayoutComboBox.SelectedIndex == 1
        ? "세벌식 최종 조합"
        : "두벌식 조합";

    private HangulKeyboardLayout SelectedKeyboardLayout => keyboardLayoutComboBox.SelectedIndex == 1
        ? HangulKeyboardLayout.SebeolsikFinal
        : HangulKeyboardLayout.Dubeolsik;

    private LegacyGlyphMode SelectedGlyphMode => legacyGlyphModeComboBox.SelectedIndex switch
    {
        1 => LegacyGlyphMode.ExtendedHangul,
        2 => LegacyGlyphMode.PrecomposedHangul,
        _ => LegacyGlyphMode.Kdaoc
    };

    private string SelectedGlyphModeName => SelectedGlyphMode switch
    {
        LegacyGlyphMode.ExtendedHangul => "확장된 한글",
        LegacyGlyphMode.PrecomposedHangul => "완성형 출력",
        _ => "KDAOC 모드"
    };

    private void UpdateRenderHookTimer()
    {
        renderHookTimer.Enabled = SelectedGlyphMode == LegacyGlyphMode.PrecomposedHangul;
        if (!renderHookTimer.Enabled)
            chatTranslation.Stop();
    }

    private void UpdateTranslationAvailability()
    {
        translationSettingsButton.Enabled =
            SelectedGlyphMode == LegacyGlyphMode.PrecomposedHangul;
    }

    private void EnsurePrecomposedRenderHook()
    {
        if (SelectedGlyphMode != LegacyGlyphMode.PrecomposedHangul)
            return;

        try
        {
            var result = renderHook.EnsureInjected(daocRootTextBox.Text.Trim());
            chatTranslation.Update(
                RegistrySettings.LoadTranslationOptions(),
                renderHook.ActiveProcessIds);
            if (result.Changed || statusLabel.Text.Contains("완성형 출력", StringComparison.Ordinal))
                statusLabel.Text = result.Message;
        }
        catch (Exception ex)
        {
            chatTranslation.Stop();
            _ = renderHook.RecordFailure(daocRootTextBox.Text.Trim(), ex);
            statusLabel.Text = "완성형 출력 불가! 게임을 재시작하세요";
        }
    }

    private void OnTranslationStatusChanged(string message)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke((Action)(() => statusLabel.Text = message));
    }

    private void OnHangulModeChanged(bool hangul)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke((Action)(() =>
        {
            UpdateLegacyHookButton();
            if (legacyHook.Enabled)
            {
                statusLabel.Text = hangul
                    ? $"한글 입력 실행 중 / {SelectedLayoutName} / {SelectedGlyphModeName} / 한글 입력 중"
                    : $"한글 입력 실행 중 / {SelectedLayoutName} / {SelectedGlyphModeName} / 영문 입력 중";
            }
        }));
    }

    private void OnInputDiagnosticChanged(string diagnostic)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke((Action)(() =>
        {
            if (legacyHook.Enabled)
                statusLabel.Text = $"{SelectedLayoutName} / {SelectedGlyphModeName} / {diagnostic}";
        }));
    }

    private void UpdateLegacyHookButton()
    {
        legacyHookButton.UseVisualStyleBackColor = false;
        if (!legacyHook.Enabled)
        {
            legacyHookButton.Text = "▶ 한글 입력 시작 — 현재 중지";
            legacyHookButton.BackColor = Color.MistyRose;
            legacyHookButton.ForeColor = Color.DarkRed;
            return;
        }

        legacyHookButton.Text = legacyHook.HangulMode
            ? "■ 한글 입력 중지 — 한글 입력 중"
            : "■ 한글 입력 중지 — 영문 입력 중";
        legacyHookButton.BackColor = Color.Honeydew;
        legacyHookButton.ForeColor = Color.DarkGreen;
    }

    private void BrowseRoot(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = daocRootTextBox.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            daocRootTextBox.Text = dialog.SelectedPath;
            SaveDaocRootPath(showWarning: true);
        }
    }

    private void SaveDaocRootPath(bool showWarning)
    {
        try
        {
            RegistrySettings.SaveDaocRootPath(daocRootTextBox.Text);
        }
        catch (Exception) when (!showWarning)
        {
            // Closing must not be blocked if the user registry is unavailable.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "DAoC 폴더 설정 저장 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void AddLabel(TableLayoutPanel panel, string text, int column, int row)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 8, 3, 3)
        }, column, row);
    }
}
