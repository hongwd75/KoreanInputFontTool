using System.Diagnostics;

namespace KoreanInputFontTool;

internal sealed class TranslationSettingsForm : Form
{
    private readonly CheckBox enabledCheckBox = new() { Text = "채팅 자동 번역 사용", AutoSize = true };
    private readonly ComboBox providerComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label apiKeyLabel = CreateFieldLabel("API 키");
    private readonly TextBox apiKeyTextBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox showApiKeyCheckBox = new() { Text = "표시", AutoSize = true };
    private readonly TextBox regionTextBox = new();
    private readonly TextBox endpointTextBox = new();
    private readonly Label regionLabel = CreateFieldLabel("지역");
    private readonly Label endpointLabel = CreateFieldLabel("서버 주소");
    private readonly CheckBox guildCheckBox = new() { Text = "길드", AutoSize = true };
    private readonly CheckBox groupCheckBox = new() { Text = "그룹", AutoSize = true };
    private readonly CheckBox whisperCheckBox = new() { Text = "귓속말", AutoSize = true };
    private readonly CheckBox sayCheckBox = new() { Text = "일반 대화", AutoSize = true };
    private readonly CheckBox lfgCheckBox = new() { Text = "LFG", AutoSize = true };
    private readonly Label installSummaryLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Button providerHelpButton = new()
    {
        Text = "설치 방법 상세",
        Size = new Size(110, 30),
        Anchor = AnchorStyles.None
    };
    private readonly Button localServerButton = new()
    {
        Text = "로컬 번역 서버 켜기",
        Size = new Size(145, 30),
        Anchor = AnchorStyles.None,
        Visible = false
    };
    private readonly ProgressBar localServerProgressBar = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 30,
        Margin = new Padding(4, 2, 4, 2),
        Visible = false
    };
    private readonly GroupBox installationGroupBox = new()
    {
        Text = "설치 방법",
        Dock = DockStyle.Top,
        Height = 88,
        Padding = new Padding(10),
        Margin = new Padding(0, 4, 0, 8)
    };
    private readonly TableLayoutPanel installationPanel = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 3,
        RowCount = 2
    };
    private readonly Label descriptionLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
        TextAlign = ContentAlignment.TopLeft
    };
    private CancellationTokenSource? localServerCancellation;

    public TranslationSettingsForm(TranslationOptions options)
    {
        Text = "번역 설정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 550);

        providerComboBox.Items.AddRange([
            "Microsoft Translator",
            "Google Cloud Translation",
            "LibreTranslate (사용자 서버)"
        ]);

        enabledCheckBox.Checked = options.Enabled;
        providerComboBox.SelectedIndex = options.Provider switch
        {
            TranslationProvider.GoogleCloudTranslation => 1,
            TranslationProvider.LibreTranslate => 2,
            _ => 0
        };
        apiKeyTextBox.Text = options.ApiKey;
        regionTextBox.Text = options.Region;
        endpointTextBox.Text = options.Endpoint;
        guildCheckBox.Checked = options.Channels.HasFlag(TranslationChannel.Guild);
        groupCheckBox.Checked = options.Channels.HasFlag(TranslationChannel.Group);
        whisperCheckBox.Checked = options.Channels.HasFlag(TranslationChannel.Whisper);
        sayCheckBox.Checked = options.Channels.HasFlag(TranslationChannel.Say);
        lfgCheckBox.Checked = options.Channels.HasFlag(TranslationChannel.Lfg);

        BuildLayout();
        enabledCheckBox.CheckedChanged += (_, _) => UpdateControls();
        providerComboBox.SelectedIndexChanged += (_, _) => UpdateControls();
        showApiKeyCheckBox.CheckedChanged += (_, _) =>
            apiKeyTextBox.UseSystemPasswordChar = !showApiKeyCheckBox.Checked;
        providerHelpButton.Click += OpenProviderHelp;
        localServerButton.Click += StartLocalServer;
        endpointTextBox.TextChanged += (_, _) => UpdateLocalServerControls();
        FormClosed += (_, _) =>
        {
            localServerCancellation?.Cancel();
            localServerCancellation?.Dispose();
        };
        UpdateControls();
    }

    public TranslationOptions Options
    {
        get
        {
            var channels = TranslationChannel.None;
            if (guildCheckBox.Checked)
                channels |= TranslationChannel.Guild;
            if (groupCheckBox.Checked)
                channels |= TranslationChannel.Group;
            if (whisperCheckBox.Checked)
                channels |= TranslationChannel.Whisper;
            if (sayCheckBox.Checked)
                channels |= TranslationChannel.Say;
            if (lfgCheckBox.Checked)
                channels |= TranslationChannel.Lfg;

            return new TranslationOptions(
                enabledCheckBox.Checked,
                SelectedProvider,
                apiKeyTextBox.Text.Trim(),
                regionTextBox.Text.Trim(),
                endpointTextBox.Text.Trim(),
                channels);
        }
    }

    private TranslationProvider SelectedProvider => providerComboBox.SelectedIndex switch
    {
        1 => TranslationProvider.GoogleCloudTranslation,
        2 => TranslationProvider.LibreTranslate,
        _ => TranslationProvider.MicrosoftTranslator
    };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(enabledCheckBox, 0, 0);

        var serviceFields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 8),
            ColumnCount = 3,
            RowCount = 4
        };
        serviceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        serviceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serviceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        for (var row = 0; row < 4; row++)
            serviceFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(serviceFields, 0, 1);

        serviceFields.Controls.Add(CreateFieldLabel("번역 방식"), 0, 0);
        providerComboBox.Dock = DockStyle.Fill;
        serviceFields.Controls.Add(providerComboBox, 1, 0);
        serviceFields.SetColumnSpan(providerComboBox, 2);

        serviceFields.Controls.Add(apiKeyLabel, 0, 1);
        apiKeyTextBox.Dock = DockStyle.Fill;
        serviceFields.Controls.Add(apiKeyTextBox, 1, 1);
        showApiKeyCheckBox.Anchor = AnchorStyles.Left;
        serviceFields.Controls.Add(showApiKeyCheckBox, 2, 1);

        serviceFields.Controls.Add(regionLabel, 0, 2);
        regionTextBox.Dock = DockStyle.Fill;
        serviceFields.Controls.Add(regionTextBox, 1, 2);
        serviceFields.SetColumnSpan(regionTextBox, 2);

        serviceFields.Controls.Add(endpointLabel, 0, 3);
        endpointTextBox.Dock = DockStyle.Fill;
        serviceFields.Controls.Add(endpointTextBox, 1, 3);
        serviceFields.SetColumnSpan(endpointTextBox, 2);

        installationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        installationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        installationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        installationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        installationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        installationPanel.Controls.Add(installSummaryLabel, 0, 0);
        installationPanel.Controls.Add(localServerButton, 1, 0);
        installationPanel.Controls.Add(providerHelpButton, 2, 0);
        installationPanel.Controls.Add(localServerProgressBar, 0, 1);
        installationPanel.SetColumnSpan(localServerProgressBar, 3);
        installationGroupBox.Controls.Add(installationPanel);
        root.Controls.Add(installationGroupBox, 0, 2);

        var channels = new GroupBox
        {
            Text = "번역할 채널",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 4, 0, 8)
        };
        var channelPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false
        };
        channelPanel.Controls.AddRange([guildCheckBox, groupCheckBox, whisperCheckBox, sayCheckBox, lfgCheckBox]);
        channels.Controls.Add(channelPanel);
        root.Controls.Add(channels, 0, 3);

        root.Controls.Add(descriptionLabel, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancelButton = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Size = new Size(86, 30) };
        var saveButton = new Button { Text = "저장", Size = new Size(86, 30) };
        saveButton.Click += Save;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);
        root.Controls.Add(buttons, 0, 5);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void UpdateControls()
    {
        var enabled = enabledCheckBox.Checked;
        providerComboBox.Enabled = enabled;
        apiKeyTextBox.Enabled = enabled;
        showApiKeyCheckBox.Enabled = enabled;
        guildCheckBox.Enabled = enabled;
        groupCheckBox.Enabled = enabled;
        whisperCheckBox.Enabled = enabled;
        sayCheckBox.Enabled = enabled;
        lfgCheckBox.Enabled = enabled;

        var usesRegion = enabled && SelectedProvider == TranslationProvider.MicrosoftTranslator;
        regionLabel.Enabled = usesRegion;
        regionTextBox.Enabled = usesRegion;

        var usesEndpoint = enabled && SelectedProvider == TranslationProvider.LibreTranslate;
        endpointLabel.Enabled = usesEndpoint;
        endpointTextBox.Enabled = usesEndpoint;

        apiKeyLabel.Text = SelectedProvider == TranslationProvider.LibreTranslate
            ? "API 키 (선택)"
            : "API 키";
        installSummaryLabel.Text = GetProviderInstallSummary();
        descriptionLabel.Text = GetProviderDescription();
        UpdateLocalServerControls();
    }

    private string GetProviderInstallSummary() => SelectedProvider switch
    {
        TranslationProvider.MicrosoftTranslator =>
            "Azure Translator 리소스의 API 키와 지역을 입력합니다.",
        TranslationProvider.GoogleCloudTranslation =>
            "Cloud Translation API(v2)를 활성화하고 발급한 API 키를 입력합니다.",
        TranslationProvider.LibreTranslate =>
            "로컬 서버를 시작하거나 외부 서버 주소와 필요한 경우 API 키를 입력합니다.",
        _ => string.Empty
    };

    private void UpdateLocalServerControls()
    {
        localServerCancellation?.Cancel();
        localServerCancellation?.Dispose();
        localServerCancellation = null;
        ShowLocalServerProgress(false);

        var endpoint = endpointTextBox.Text.Trim();
        var isSupportedLocalServer =
            SelectedProvider == TranslationProvider.LibreTranslate &&
            LibreTranslateLocalServerService.IsSupportedLocalEndpoint(endpoint);
        localServerButton.Visible = isSupportedLocalServer;
        if (!isSupportedLocalServer)
            return;

        localServerButton.Enabled = false;
        localServerButton.Text = "서버 확인 중...";
        var cancellation = new CancellationTokenSource();
        localServerCancellation = cancellation;
        _ = RefreshLocalServerStatusAsync(endpoint, cancellation.Token);
    }

    private async Task RefreshLocalServerStatusAsync(string endpoint, CancellationToken cancellationToken)
    {
        bool available;
        try
        {
            available = await LibreTranslateLocalServerService
                .IsAvailableAsync(endpoint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested || IsDisposed)
            return;

        localServerButton.Enabled = !available;
        localServerButton.Text = available
            ? "로컬 서버 실행 중"
            : "로컬 번역 서버 켜기";
    }

    private async void StartLocalServer(object? sender, EventArgs e)
    {
        localServerCancellation?.Cancel();
        localServerCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        localServerCancellation = cancellation;
        localServerButton.Enabled = false;
        localServerButton.Text = "서버 시작 중...";
        ShowLocalServerProgress(true);

        try
        {
            var progress = new Progress<string>(message => installSummaryLabel.Text = message);
            await LibreTranslateLocalServerService.StartAsync(
                endpointTextBox.Text.Trim(),
                progress,
                cancellation.Token);
            installSummaryLabel.Text = "로컬 LibreTranslate 서버가 실행 중입니다.";
            localServerButton.Text = "로컬 서버 실행 중";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            installSummaryLabel.Text = "로컬 서버를 시작하지 못했습니다. 상세 설치 방법을 확인하세요.";
            localServerButton.Enabled = true;
            localServerButton.Text = "로컬 번역 서버 켜기";
            MessageBox.Show(
                this,
                ex.Message,
                "로컬 번역 서버 시작 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (ReferenceEquals(localServerCancellation, cancellation))
            {
                localServerCancellation = null;
                cancellation.Dispose();
                ShowLocalServerProgress(false);
            }
        }
    }

    private void ShowLocalServerProgress(bool visible)
    {
        localServerProgressBar.Visible = visible;
        localServerProgressBar.MarqueeAnimationSpeed = visible ? 30 : 0;
        installationPanel.RowStyles[1].Height = visible ? 22 : 0;
        installationGroupBox.Height = visible ? 116 : 88;
    }

    private void OpenProviderHelp(object? sender, EventArgs e)
    {
        var url = SelectedProvider switch
        {
            TranslationProvider.MicrosoftTranslator =>
                "https://github.com/hongwd75/KoreanInputFontTool/blob/main/docs/translation/microsoft-translator.md",
            TranslationProvider.GoogleCloudTranslation =>
                "https://github.com/hongwd75/KoreanInputFontTool/blob/main/docs/translation/google-cloud-translation.md",
            TranslationProvider.LibreTranslate =>
                "https://github.com/hongwd75/KoreanInputFontTool/blob/main/docs/translation/libretranslate.md",
            _ => "https://github.com/hongwd75/KoreanInputFontTool/tree/main/docs/translation"
        };

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"설치 안내 페이지를 열지 못했습니다.\r\n{url}\r\n\r\n{ex.Message}",
                "설치 방법 상세",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private string GetProviderDescription()
    {
        var providerDescription = SelectedProvider switch
        {
            TranslationProvider.MicrosoftTranslator =>
                "Azure Translator API 키와 지역이 필요합니다. 사용량은 Azure 계정에서 관리됩니다.",
            TranslationProvider.GoogleCloudTranslation =>
                "Google Cloud Translation API 키가 필요합니다. API 활성화와 결제 설정이 필요할 수 있습니다.",
            TranslationProvider.LibreTranslate =>
                "LibreTranslate 서버 주소를 사용합니다. 로컬 서버는 보통 API 키가 필요 없으며, 외부 서버는 API 키가 필요할 수 있습니다.",
            _ => string.Empty
        };

        return providerDescription + "\r\n" +
               "한글이 없는 영문 메시지만 번역하며, 원문 다음에 [번역] : 내용으로 표시합니다.\r\n" +
               "게임 채팅 연결은 완성형 출력 모드에서만 동작합니다.";
    }

    private void Save(object? sender, EventArgs e)
    {
        var options = Options;
        if (options.Enabled && options.Channels == TranslationChannel.None)
        {
            MessageBox.Show(this, "번역할 채널을 하나 이상 선택하세요.", "번역 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (options.Enabled &&
            options.Provider is TranslationProvider.MicrosoftTranslator or TranslationProvider.GoogleCloudTranslation &&
            options.ApiKey.Length == 0)
        {
            MessageBox.Show(this, "선택한 번역 방식의 API 키를 입력하세요.", "번역 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            apiKeyTextBox.Focus();
            return;
        }

        if (options.Enabled && options.Provider == TranslationProvider.LibreTranslate &&
            (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
        {
            MessageBox.Show(this, "올바른 LibreTranslate 서버 주소를 입력하세요.", "번역 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            endpointTextBox.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(3, 8, 3, 3)
    };
}
