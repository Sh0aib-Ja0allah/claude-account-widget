using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Shapes = System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Optional test arguments: <data folder> <account file> [<credentials file> <usage url> <poll seconds>]
        bool isTest = args.Length >= 2;
        bool created = true;
        Mutex mutex = isTest ? null : new Mutex(true, "ClaudeAccountWidget.SingleInstance", out created);
        if (!created) return;

        string dataDir = isTest ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        string accountFile = isTest
            ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        string credentialsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        string usageUrl = "https://api.anthropic.com/api/oauth/usage";
        int pollSeconds = 300;
        int manualGapSeconds = 30;
        if (isTest)
        {
            bool testsFetching = args.Length == 5;
            credentialsFile = testsFetching ? args[2] : null;
            usageUrl = testsFetching ? args[3] : null;
            pollSeconds = testsFetching ? int.Parse(args[4], CultureInfo.InvariantCulture) : 300;
            manualGapSeconds = 0;
        }

        new Application().Run(new WidgetWindow(dataDir, accountFile, credentialsFile, usageUrl, pollSeconds, manualGapSeconds));
        GC.KeepAlive(mutex);
    }
}

enum FetchStatus { Ok, RateLimited, TokenRejected, TokenExpired, Failed }

class FetchResult
{
    public FetchStatus Status;
    public int RetryAfterSeconds;

    public FetchResult(FetchStatus status, int retryAfterSeconds = 0)
    {
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

// Reads the login token and asks Anthropic for usage. It never renews the token or writes to the credentials file.
static class UsageClient
{
    public static FetchResult Fetch(string credentialsFile, string url, string account, string usageFile)
    {
        try
        {
            string token = ReadToken(credentialsFile);
            if (token == null) return new FetchResult(FetchStatus.TokenExpired);

            string body = Download(url, token);
            string data = BuildUsageJson(body, account);
            if (data == null) return new FetchResult(FetchStatus.Failed);

            string temp = usageFile + ".widget.tmp";
            File.WriteAllText(temp, data);
            if (File.Exists(usageFile)) File.Replace(temp, usageFile, null);
            else File.Move(temp, usageFile);
            return new FetchResult(FetchStatus.Ok);
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response == null) return new FetchResult(FetchStatus.Failed);
            using (response)
            {
                int code = (int)response.StatusCode;
                if (code == 429)
                {
                    int wait;
                    int.TryParse(response.Headers["Retry-After"], out wait);
                    return new FetchResult(FetchStatus.RateLimited, wait);
                }
                return new FetchResult(code == 401 || code == 403 ? FetchStatus.TokenRejected : FetchStatus.Failed);
            }
        }
        catch (Exception)
        {
            return new FetchResult(FetchStatus.Failed);
        }
    }

    // Returns a label such as "Max", or null when the login has no plan name. Only the plan fields are kept.
    public static string ReadPlan(string credentialsFile)
    {
        string text;
        using (FileStream stream = new FileStream(credentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        Dictionary<string, object> oauth = Section(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text), "claudeAiOauth");
        if (oauth == null) return null;

        object type;
        if (!oauth.TryGetValue("subscriptionType", out type) || !(type is string) || ((string)type).Length == 0) return null;
        string name = (string)type;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    static string ReadToken(string credentialsFile)
    {
        string text;
        using (FileStream stream = new FileStream(credentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        Dictionary<string, object> root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
        Dictionary<string, object> oauth = Section(root, "claudeAiOauth");
        if (oauth == null) return null;

        object token;
        if (!oauth.TryGetValue("accessToken", out token) || !(token is string)) return null;

        object expires;
        if (oauth.TryGetValue("expiresAt", out expires) && expires != null)
        {
            double expiresMs = Convert.ToDouble(expires, CultureInfo.InvariantCulture);
            double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            if (expiresMs <= nowMs + 60000) return null;
        }
        return (string)token;
    }

    static string Download(string url, string token)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        request.AllowAutoRedirect = false;
        request.UserAgent = "ClaudeAccountWidget/1.0";
        request.Accept = "application/json";
        request.Headers["Authorization"] = "Bearer " + token;
        request.Headers["anthropic-beta"] = "oauth-2025-04-20";

        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            if ((int)response.StatusCode != 200) throw new InvalidDataException();
            return reader.ReadToEnd();
        }
    }

    static string BuildUsageJson(string body, string account)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;
        Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(body);

        // A reply without these keys means the undocumented format changed, so keep the last good numbers.
        if (root == null || !root.ContainsKey("five_hour") || !root.ContainsKey("seven_day")) return null;

        double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        return string.Format(CultureInfo.InvariantCulture,
            "{{\"account\":{0},\"updated\":{1:F0},\"five_hour\":{2},\"seven_day\":{3}}}",
            serializer.Serialize(account), nowMs, WindowJson(root, "five_hour"), WindowJson(root, "seven_day"));
    }

    static string WindowJson(Dictionary<string, object> root, string key)
    {
        Dictionary<string, object> window = Section(root, key);
        if (window == null) return "null";

        double used = Convert.ToDouble(window["utilization"], CultureInfo.InvariantCulture);
        double resets = 0;
        object resetValue;
        if (window.TryGetValue("resets_at", out resetValue) && resetValue is string)
        {
            DateTimeOffset at = DateTimeOffset.Parse((string)resetValue, CultureInfo.InvariantCulture);
            resets = (at.UtcDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
        return "{\"used\":" + used.ToString("R", CultureInfo.InvariantCulture)
            + ",\"resets\":" + resets.ToString("F0", CultureInfo.InvariantCulture) + "}";
    }

    static Dictionary<string, object> Section(Dictionary<string, object> root, string key)
    {
        object value;
        if (root == null || !root.TryGetValue(key, out value)) return null;
        return value as Dictionary<string, object>;
    }
}

class UsageRow
{
    public readonly Grid Fill = new Grid();
    public readonly Border Track = new Border();
    public readonly ColumnDefinition Used = new ColumnDefinition();
    public readonly ColumnDefinition Free = new ColumnDefinition();
    public readonly Border Bar = new Border();
    public readonly TextBlock Percent = new TextBlock();
    public readonly TextBlock Reset = new TextBlock();
    public readonly StackPanel Panel = new StackPanel();
}

class WidgetWindow : Window
{
    // Optional: the email in expected-account.txt, next to the app. Without it, no account triggers a warning.
    string expectedAccount = "";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ClaudeAccountWidget";

    readonly string usageFile;
    readonly string settingsFile;
    readonly string accountFile;
    readonly string credentialsFile;
    readonly string usageUrl;
    readonly TimeSpan manualGap;

    bool fetching;
    DateTime lastAttempt = DateTime.MinValue;
    DateTime blockedUntil = DateTime.MinValue;
    DateTime rejectedStamp = DateTime.MinValue;
    TimeSpan backoff = TimeSpan.FromMinutes(10);

    readonly Border dot = new Border();
    readonly TextBlock email = new TextBlock();
    readonly TextBlock accountNote = new TextBlock();
    readonly UsageRow fiveHour;
    readonly UsageRow sevenDay;
    readonly TextBlock emptyNote = new TextBlock();
    readonly TextBlock updated = new TextBlock();
    readonly TextBlock checkNote = new TextBlock();
    readonly TextBlock refreshIcon = new TextBlock();
    readonly TextBlock eyeIcon = new TextBlock();
    readonly Border miniBadge = new Border();
    readonly TextBlock miniPercent = new TextBlock();
    Grid miniIcon;
    UIElement card;
    bool collapsed;
    const double FullWidth = 316;
    const double MiniWidth = 100;
    const double MiniHeight = 56;
    const double GaugeRadius = 13.5;
    const string ClaudeSpark = "M4.709 15.955l4.72-2.647.08-.23-.08-.128H9.2l-.79-.048-2.698-.073-2.339-.097-2.266-.122-.571-.121L0 11.784l.055-.352.48-.321.686.06 1.52.103 2.278.158 1.652.097 2.449.255h.389l.055-.157-.134-.098-.103-.097-2.358-1.596-2.552-1.688-1.336-.972-.724-.491-.364-.462-.158-1.008.656-.722.881.06.225.061.893.686 1.908 1.476 2.491 1.833.365.304.145-.103.019-.073-.164-.274-1.355-2.446-1.446-2.49-.644-1.032-.17-.619a2.97 2.97 0 01-.104-.729L6.283.134 6.696 0l.996.134.42.364.62 1.414 1.002 2.229 1.555 3.03.456.898.243.832.091.255h.158V9.01l.128-1.706.237-2.095.23-2.695.08-.76.376-.91.747-.492.584.28.48.685-.067.444-.286 1.851-.559 2.903-.364 1.942h.212l.243-.242.985-1.306 1.652-2.064.73-.82.85-.904.547-.431h1.033l.76 1.129-.34 1.166-1.064 1.347-.881 1.142-1.264 1.7-.79 1.36.073.11.188-.02 2.856-.606 1.543-.28 1.841-.315.833.388.091.395-.328.807-1.969.486-2.309.462-3.439.813-.042.03.049.061 1.549.146.662.036h1.622l3.02.225.79.522.474.638-.079.485-1.215.62-1.64-.389-3.829-.91-1.312-.329h-.182v.11l1.093 1.068 2.006 1.81 2.509 2.33.127.578-.322.455-.34-.049-2.205-1.657-.851-.747-1.926-1.62h-.128v.17l.444.649 2.345 3.521.122 1.08-.17.353-.608.213-.668-.122-1.374-1.925-1.415-2.167-1.143-1.943-.14.08-.674 7.254-.316.37-.729.28-.607-.461-.322-.747.322-1.476.389-1.924.315-1.53.286-1.9.17-.632-.012-.042-.14.018-1.434 1.967-2.18 2.945-1.726 1.845-.414.164-.717-.37.067-.662.401-.589 2.388-3.036 1.44-1.882.93-1.086-.006-.158h-.055L4.132 18.56l-1.13.146-.487-.456.061-.746.231-.243 1.908-1.312-.006.006z";
    readonly Shapes.Path miniArc = new Shapes.Path();
    readonly Shapes.Path heroArc = new Shapes.Path();
    readonly Shapes.Path heroTick = new Shapes.Path();
    readonly DropShadowEffect heroGlow = new DropShadowEffect();
    readonly TextBlock heroNumber = new TextBlock();
    readonly TextBlock heroCaption = new TextBlock();
    readonly Border paceChip = new Border();
    readonly TextBlock paceText = new TextBlock();
    readonly TextBlock leftText = new TextBlock();
    readonly Border refreshButton = new Border();
    readonly Border eyeButton = new Border();
    readonly Border miniPill = new Border();
    readonly MenuItem accentItem = new MenuItem();
    bool switching;
    string accentName = "default";
    string themeSetting = "system";
    bool? lightApplied;
    Brush arcNormal;
    System.Windows.Media.Color glowNormal;

    static readonly string[,] Accents =
    {
        { "default", "Default", "" },
        { "orange", "Claude orange", "#D97757" },
        { "blue", "Blue", "#5B9BF5" },
        { "green", "Green", "#4FBF7F" },
        { "purple", "Purple", "#A78BFA" },
        { "pink", "Pink", "#F06BA8" },
    };
    const double HeroSize = 92;
    const double HeroRadius = 42.5;
    DateTime? sevenReset;
    double gaugeShown;
    double gaugeTarget = -1;
    Brush gaugeBrush;
    DispatcherTimer gaugeTimer;
    Border cardBorder;
    readonly Border planChip = new Border();
    readonly TextBlock planText = new TextBlock();
    DateTime planStamp = DateTime.MinValue;
    string accountPlan;
    string credentialsPlan;
    readonly Border divider = new Border();
    Brush cardEdge;
    readonly RotateTransform refreshSpin = new RotateTransform();
    readonly MenuItem topmostItem = new MenuItem();
    readonly MenuItem startupItem = new MenuItem();

    string account;
    bool accountKnown;
    DateTime accountStamp = DateTime.MinValue;

    static Brush Color(string hex)
    {
        Brush brush = (Brush)new BrushConverter().ConvertFromString(hex);
        brush.Freeze();
        return brush;
    }

    static SolidColorBrush Live(string hex)
    {
        return new SolidColorBrush(Hex(hex));
    }

    static readonly SolidColorBrush Text = Live("#F2F2F0");
    static readonly SolidColorBrush Muted = Live("#A3A3A0");
    static readonly SolidColorBrush Track = Live("#3A3B3F");
    static readonly SolidColorBrush Good = Live("#6FBF8B");
    static readonly SolidColorBrush Warn = Live("#E5B454");
    static readonly SolidColorBrush Danger = Live("#EF6F6C");
    static readonly SolidColorBrush OverlaySoft = Live("#1FFFFFFF");
    static readonly SolidColorBrush OverlayMid = Live("#26FFFFFF");
    static readonly SolidColorBrush OverlayTrack = Live("#2EFFFFFF");
    static readonly SolidColorBrush TickBrush = Live("#D9FFFFFF");
    static readonly SolidColorBrush CardEdge = Live("#3D3F45");
    static readonly SolidColorBrush PillEdge = Live("#44464C");
    static readonly SolidColorBrush PillHover = Live("#73757D");
    static readonly SolidColorBrush BadgeEdge = Live("#17181B");

    // Matches the right-click menu to the widget. ApplyTheme supplies its colors.
    const string MenuXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <ControlTemplate x:Key='MenuPanel' TargetType='ContentControl'>
    <Border Margin='10' MinWidth='172' Padding='4' CornerRadius='12' BorderThickness='1'
            Background='{DynamicResource MenuBackground}' BorderBrush='{DynamicResource MenuEdge}'>
      <Border.Effect>
        <DropShadowEffect BlurRadius='16' ShadowDepth='3' Direction='270' Opacity='0.4'/>
      </Border.Effect>
      <ContentPresenter/>
    </Border>
  </ControlTemplate>

  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='FontFamily' Value='Segoe UI'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <ContentControl Template='{StaticResource MenuPanel}' Focusable='False'>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </ContentControl>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Margin='8,4' Background='{DynamicResource MenuHover}'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource MenuText}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Grid SnapsToDevicePixels='True'>
            <Border x:Name='Row' CornerRadius='8' Background='Transparent' Padding='8,6,10,6'>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='20'/>
                  <ColumnDefinition Width='Auto'/>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <TextBlock x:Name='Check' Text='&#xE73E;' FontFamily='Segoe MDL2 Assets' FontSize='11'
                           VerticalAlignment='Center' Visibility='Collapsed'/>
                <ContentPresenter x:Name='Swatch' Grid.Column='1' ContentSource='Icon' Margin='0,0,8,0' VerticalAlignment='Center'/>
                <ContentPresenter Grid.Column='2' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
                <TextBlock x:Name='Arrow' Grid.Column='3' Text='&#xE76C;' FontFamily='Segoe MDL2 Assets' FontSize='9'
                           Margin='16,0,0,0' VerticalAlignment='Center' Foreground='{DynamicResource MenuMuted}' Visibility='Collapsed'/>
              </Grid>
            </Border>
            <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='-8' VerticalOffset='-15'
                   AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'
                   IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <ContentControl Template='{StaticResource MenuPanel}' Focusable='False' FontFamily='Segoe UI' FontSize='12.5'>
                <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
              </ContentControl>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='Icon' Value='{x:Null}'>
              <Setter TargetName='Swatch' Property='Visibility' Value='Collapsed'/>
            </Trigger>
            <Trigger Property='Role' Value='SubmenuHeader'>
              <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Row' Property='Background' Value='{DynamicResource MenuHover}'/>
            </Trigger>
            <Trigger Property='IsSubmenuOpen' Value='True'>
              <Setter TargetName='Row' Property='Background' Value='{DynamicResource MenuHover}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    static void ApplyMenuStyle()
    {
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(MenuXaml));
    }

    public WidgetWindow(string dataDir, string accountPath, string credentialsPath, string url, int pollSeconds, int manualGapSeconds)
    {
        credentialsFile = credentialsPath;
        usageUrl = url;
        manualGap = TimeSpan.FromSeconds(manualGapSeconds);
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        Directory.CreateDirectory(dataDir);
        usageFile = Path.Combine(dataDir, "usage.json");
        settingsFile = Path.Combine(dataDir, "settings.txt");
        try
        {
            string expectedFile = Path.Combine(dataDir, "expected-account.txt");
            if (File.Exists(expectedFile)) expectedAccount = File.ReadAllText(expectedFile).Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        accountFile = accountPath;

        Title = "Claude Account Widget";
        Width = FullWidth;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;

        fiveHour = BuildRow("5-hour limit");
        sevenDay = BuildHero();
        card = BuildCard();
        miniIcon = BuildMiniIcon();
        Content = card;
        ApplyMenuStyle();
        ContextMenu = BuildMenu();
        LoadSettings();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += delegate
        {
            Dispatcher.BeginInvoke(new Action(ApplyThemeAndRefresh));
        };

        MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            double startLeft = Left;
            double startTop = Top;
            DragMove();

            // A press that barely moves the small icon counts as a click.
            bool moved = Math.Abs(Left - startLeft) > 4 || Math.Abs(Top - startTop) > 4;
            if (collapsed && !moved)
            {
                Left = startLeft;
                Top = startTop;
                Expand();
            }
            else
            {
                SaveSettings();
            }
        };

        DispatcherTimer timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromSeconds(10);
        timer.Tick += delegate { ApplyThemeAndRefresh(); };
        timer.Start();
        Refresh();

        DispatcherTimer pollTimer = new DispatcherTimer();
        pollTimer.Interval = TimeSpan.FromSeconds(pollSeconds);
        pollTimer.Tick += delegate { FetchUsage(false); };
        pollTimer.Start();
        Loaded += delegate { FetchUsage(false); };
    }

    UIElement BuildCard()
    {
        Shapes.Path spark = BuildSpark(22);
        spark.Margin = new Thickness(0, 0, 8, 0);

        email.Foreground = Text;
        email.FontSize = 14;
        email.FontWeight = FontWeights.SemiBold;
        email.VerticalAlignment = VerticalAlignment.Center;
        email.TextTrimming = TextTrimming.CharacterEllipsis;

        BuildRefreshIcon();
        BuildEyeIcon();

        DockPanel header = new DockPanel();
        header.Children.Add(eyeButton);
        header.Children.Add(refreshButton);
        header.Children.Add(email);

        dot.Width = 7;
        dot.Height = 7;
        dot.CornerRadius = new CornerRadius(4);
        dot.Margin = new Thickness(0, 0, 6, 0);
        dot.VerticalAlignment = VerticalAlignment.Center;

        accountNote.FontSize = 11;

        StackPanel status = new StackPanel();
        status.Orientation = Orientation.Horizontal;
        status.Margin = new Thickness(0, 2, 0, 0);
        status.Children.Add(dot);
        status.Children.Add(accountNote);

        planText.Foreground = Text;
        planText.FontSize = 10;
        planText.FontWeight = FontWeights.SemiBold;

        planChip.Background = OverlaySoft;
        planChip.CornerRadius = new CornerRadius(7);
        planChip.Padding = new Thickness(7, 1, 7, 1);
        planChip.Margin = new Thickness(8, 0, 0, 0);
        planChip.VerticalAlignment = VerticalAlignment.Center;
        planChip.Visibility = Visibility.Collapsed;
        planChip.Child = planText;
        status.Children.Add(planChip);

        emptyNote.Foreground = Muted;
        emptyNote.FontSize = 12;
        emptyNote.TextWrapping = TextWrapping.Wrap;
        emptyNote.Margin = new Thickness(0, 0, 0, 12);

        divider.Height = 1;
        divider.Background = OverlaySoft;
        divider.Margin = new Thickness(0, 0, 0, 9);

        updated.Foreground = Muted;
        updated.FontSize = 11;

        checkNote.Foreground = Warn;
        checkNote.FontSize = 11;
        checkNote.TextWrapping = TextWrapping.Wrap;
        checkNote.Margin = new Thickness(0, 2, 0, 0);
        checkNote.Visibility = Visibility.Collapsed;

        StackPanel stack = new StackPanel();
        // The logo sits to the left of both text lines, centered between them.
        StackPanel accountText = new StackPanel();
        accountText.Children.Add(header);
        accountText.Children.Add(status);

        DockPanel accountBlock = new DockPanel();
        accountBlock.Margin = new Thickness(0, 0, 0, 22);
        accountBlock.Children.Add(spark);
        accountBlock.Children.Add(accountText);

        stack.Children.Add(accountBlock);
        stack.Children.Add(sevenDay.Panel);
        stack.Children.Add(fiveHour.Panel);
        stack.Children.Add(emptyNote);
        stack.Children.Add(divider);
        stack.Children.Add(updated);
        stack.Children.Add(checkNote);

        DropShadowEffect shadow = new DropShadowEffect();
        shadow.BlurRadius = 12;
        shadow.ShadowDepth = 2;
        shadow.Direction = 270;
        shadow.Opacity = 0.5;

        cardEdge = CardEdge;
        cardBorder = new Border();
        cardBorder.Margin = new Thickness(8);
        cardBorder.Background = CardFill(false);
        cardBorder.BorderBrush = cardEdge;
        cardBorder.BorderThickness = new Thickness(1);
        cardBorder.CornerRadius = new CornerRadius(16);
        cardBorder.Padding = new Thickness(16, 10, 16, 12);
        cardBorder.Effect = shadow;
        cardBorder.Child = stack;
        return cardBorder;
    }

    static Shapes.Path BuildSpark(double size)
    {
        Shapes.Path spark = new Shapes.Path();
        spark.Data = Geometry.Parse(ClaudeSpark);
        spark.Fill = Color("#D97757");
        spark.Stretch = Stretch.Uniform;
        spark.Width = size;
        spark.Height = size;
        spark.HorizontalAlignment = HorizontalAlignment.Center;
        spark.VerticalAlignment = VerticalAlignment.Center;
        return spark;
    }

    UsageRow BuildHero()
    {
        UsageRow row = new UsageRow();

        Shapes.Ellipse track = new Shapes.Ellipse();
        track.Stroke = OverlayMid;
        track.StrokeThickness = 7;

        heroArc.StrokeThickness = 7;
        heroArc.StrokeStartLineCap = PenLineCap.Round;
        heroArc.StrokeEndLineCap = PenLineCap.Round;
        heroGlow.BlurRadius = 10;
        heroGlow.ShadowDepth = 0;
        heroGlow.Opacity = 0.35;
        heroArc.Effect = heroGlow;

        // Marks how much of the 7-day window has passed.
        heroTick.Stroke = TickBrush;
        heroTick.StrokeThickness = 2;
        heroTick.StrokeStartLineCap = PenLineCap.Round;
        heroTick.StrokeEndLineCap = PenLineCap.Round;
        heroTick.ToolTip = "Time passed in this 7-day window";

        heroNumber.Foreground = Text;
        heroNumber.FontWeight = FontWeights.SemiBold;
        heroNumber.FontSize = 25;
        heroNumber.HorizontalAlignment = HorizontalAlignment.Center;

        heroCaption.Text = "used";
        heroCaption.Foreground = Muted;
        heroCaption.FontSize = 10;
        heroCaption.Margin = new Thickness(0, -3, 0, 0);
        heroCaption.HorizontalAlignment = HorizontalAlignment.Center;

        StackPanel center = new StackPanel();
        center.VerticalAlignment = VerticalAlignment.Center;
        center.Children.Add(heroNumber);
        center.Children.Add(heroCaption);

        Grid gauge = new Grid();
        gauge.Width = HeroSize;
        gauge.Height = HeroSize;
        gauge.Margin = new Thickness(0, 0, 16, 0);
        gauge.Children.Add(track);
        gauge.Children.Add(heroArc);
        gauge.Children.Add(heroTick);
        gauge.Children.Add(center);

        TextBlock name = new TextBlock();
        name.Text = "7-day limit";
        name.Foreground = Text;
        name.FontSize = 15;
        name.FontWeight = FontWeights.SemiBold;

        row.Reset.Foreground = Muted;
        row.Reset.FontSize = 12;
        row.Reset.TextWrapping = TextWrapping.Wrap;
        row.Reset.Margin = new Thickness(0, 2, 0, 0);

        paceText.FontSize = 10.5;
        paceText.FontWeight = FontWeights.SemiBold;
        paceChip.Background = OverlaySoft;
        paceChip.CornerRadius = new CornerRadius(8);
        paceChip.Padding = new Thickness(8, 2, 8, 2);
        paceChip.Child = paceText;

        leftText.Foreground = Muted;
        leftText.FontSize = 11;
        leftText.Margin = new Thickness(7, 0, 0, 0);
        leftText.VerticalAlignment = VerticalAlignment.Center;

        StackPanel paceRow = new StackPanel();
        paceRow.Orientation = Orientation.Horizontal;
        paceRow.Margin = new Thickness(0, 8, 0, 0);
        paceRow.Children.Add(paceChip);
        paceRow.Children.Add(leftText);

        StackPanel words = new StackPanel();
        words.VerticalAlignment = VerticalAlignment.Center;
        words.Children.Add(name);
        words.Children.Add(row.Reset);
        words.Children.Add(paceRow);

        DockPanel line = new DockPanel();
        line.Children.Add(gauge);
        line.Children.Add(words);

        row.Panel.Margin = new Thickness(0, 0, 0, 16);
        row.Panel.Children.Add(line);
        return row;
    }

    void BuildHeaderButton(Border button, TextBlock icon, string glyph, string tip, double rightMargin)
    {
        icon.Text = glyph;
        icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
        icon.FontSize = 13;
        icon.Foreground = Muted;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        Brush hover = OverlayMid;
        button.Width = 26;
        button.Height = 26;
        button.CornerRadius = new CornerRadius(13);
        button.Background = Brushes.Transparent;
        button.Margin = new Thickness(2, -5, rightMargin, -5);
        button.Cursor = Cursors.Hand;
        button.ToolTip = tip;
        button.Child = icon;
        DockPanel.SetDock(button, Dock.Right);
        button.MouseEnter += delegate
        {
            button.Background = hover;
            icon.Foreground = Text;
        };
        button.MouseLeave += delegate
        {
            button.Background = Brushes.Transparent;
            icon.Foreground = Muted;
        };
    }

    void BuildRefreshIcon()
    {
        BuildHeaderButton(refreshButton, refreshIcon, "\uE72C", "Refresh now", 0);
        refreshIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        refreshIcon.RenderTransform = refreshSpin;
        refreshButton.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            // Keeps the click from starting a window drag.
            e.Handled = true;
            RefreshNow();
            refreshSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(500))));
        };
    }

    void BuildEyeIcon()
    {
        BuildHeaderButton(eyeButton, eyeIcon, "\uE890", "Minimize to icon", -6);
        eyeButton.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            // Keeps the click from starting a window drag.
            e.Handled = true;
            Collapse();
        };
    }

    Grid BuildMiniIcon()
    {
        Shapes.Ellipse track = new Shapes.Ellipse();
        track.Stroke = OverlayTrack;
        track.StrokeThickness = 3;

        miniArc.StrokeThickness = 3;
        miniArc.StrokeStartLineCap = PenLineCap.Round;
        miniArc.StrokeEndLineCap = PenLineCap.Round;

        Shapes.Path spark = BuildSpark(15);

        Grid gauge = new Grid();
        gauge.Width = 30;
        gauge.Height = 30;
        gauge.Margin = new Thickness(5, 0, 7, 0);
        gauge.Children.Add(track);
        gauge.Children.Add(miniArc);
        gauge.Children.Add(spark);

        miniPercent.Text = "--";
        miniPercent.Foreground = Muted;
        miniPercent.FontSize = 15;
        miniPercent.FontWeight = FontWeights.SemiBold;
        miniPercent.VerticalAlignment = VerticalAlignment.Center;

        StackPanel row = new StackPanel();
        row.Orientation = Orientation.Horizontal;
        row.Children.Add(gauge);
        row.Children.Add(miniPercent);

        Brush edge = PillEdge;
        Brush edgeHover = PillHover;

        Border pill = miniPill;
        pill.Width = 84;
        pill.Height = 40;
        pill.CornerRadius = new CornerRadius(20);
        pill.Background = PillFill(false);
        pill.BorderBrush = edge;
        pill.BorderThickness = new Thickness(1);
        pill.Child = row;

        DropShadowEffect shadow = new DropShadowEffect();
        shadow.BlurRadius = 12;
        shadow.ShadowDepth = 2;
        shadow.Direction = 270;
        shadow.Opacity = 0.45;
        pill.Effect = shadow;

        // Keeps a wrong-account warning visible while the widget is minimized.
        miniBadge.Width = 11;
        miniBadge.Height = 11;
        miniBadge.CornerRadius = new CornerRadius(6);
        miniBadge.BorderBrush = BadgeEdge;
        miniBadge.BorderThickness = new Thickness(2);
        miniBadge.HorizontalAlignment = HorizontalAlignment.Right;
        miniBadge.VerticalAlignment = VerticalAlignment.Top;
        miniBadge.Margin = new Thickness(0, 7, 12, 0);
        miniBadge.Visibility = Visibility.Collapsed;

        Grid grid = new Grid();
        grid.Width = MiniWidth;
        grid.Height = MiniHeight;
        grid.Cursor = Cursors.Hand;
        grid.Children.Add(pill);
        grid.Children.Add(miniBadge);
        grid.MouseEnter += delegate { pill.BorderBrush = edgeHover; };
        grid.MouseLeave += delegate { pill.BorderBrush = edge; };
        return grid;
    }

    static System.Windows.Media.Color Hex(string hex)
    {
        return (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
    }

    static readonly Brush ArcWarn = Gradient("#F5CE7C", "#E2A437");
    static readonly Brush ArcDanger = Gradient("#F89490", "#E5504C");

    static Brush Gradient(string from, string to)
    {
        LinearGradientBrush brush = new LinearGradientBrush(Hex(from), Hex(to), 90);
        brush.Freeze();
        return brush;
    }

    // Eases both rings to a new value, so a change in usage is visible.
    void SetGauges(double target, Brush brush, System.Windows.Media.Color glow)
    {
        gaugeBrush = brush;
        heroGlow.Color = glow;
        if (Math.Abs(target - gaugeTarget) < 0.01)
        {
            DrawGauges(gaugeShown);
            return;
        }

        gaugeTarget = target;
        double from = gaugeShown;
        DateTime started = DateTime.UtcNow;
        if (gaugeTimer != null) gaugeTimer.Stop();
        gaugeTimer = new DispatcherTimer();
        gaugeTimer.Interval = TimeSpan.FromMilliseconds(16);
        gaugeTimer.Tick += delegate
        {
            double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 600);
            double eased = 1 - Math.Pow(1 - t, 3);
            gaugeShown = from + (target - from) * eased;
            DrawGauges(gaugeShown);
            if (t >= 1) gaugeTimer.Stop();
        };
        gaugeTimer.Start();
    }

    void DrawGauges(double percent)
    {
        SetArc(miniArc, 30, GaugeRadius, percent, gaugeBrush);
        SetArc(heroArc, HeroSize, HeroRadius, percent, gaugeBrush);
    }

    void UpdatePace(bool known, double used)
    {
        bool show = known && sevenReset.HasValue;
        paceChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        leftText.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        leftText.Margin = new Thickness(show ? 7 : 0, 0, 0, 0);
        leftText.Text = Math.Round(100 - used).ToString(CultureInfo.InvariantCulture) + "% left";
        if (!show)
        {
            heroTick.Data = null;
            return;
        }

        // Assumes the window started exactly 7 days before it resets.
        double elapsed = 100 * (1 - (sevenReset.Value - DateTime.UtcNow).TotalDays / 7);
        elapsed = Math.Max(0, Math.Min(100, elapsed));
        double gap = used - elapsed;
        paceText.Text = gap > 5 ? "Above pace" : gap < -5 ? "Below pace" : "On pace";
        paceText.Foreground = gap > 5 ? Warn : gap < -5 ? Good : Text;

        double radians = elapsed / 100 * 2 * Math.PI;
        double center = HeroSize / 2;
        Point inner = new Point(center + (HeroRadius - 5.5) * Math.Sin(radians), center - (HeroRadius - 5.5) * Math.Cos(radians));
        Point outer = new Point(center + (HeroRadius + 5.5) * Math.Sin(radians), center - (HeroRadius + 5.5) * Math.Cos(radians));
        LineGeometry tick = new LineGeometry(inner, outer);
        tick.Freeze();
        heroTick.Data = tick;
    }

    static void SetArc(Shapes.Path path, double size, double radius, double percent, Brush color)
    {
        path.Stroke = color;
        double sweep = Math.Min(percent, 99.99) / 100 * 360;
        if (sweep <= 0)
        {
            path.Data = null;
            return;
        }

        double center = size / 2;
        double radians = sweep * Math.PI / 180;
        Point start = new Point(center, center - radius);
        Point end = new Point(center + radius * Math.Sin(radians), center - radius * Math.Cos(radians));

        StreamGeometry arc = new StreamGeometry();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        path.Data = arc;
    }

    static Brush CardFill(bool light)
    {
        // A soft gradient with a faint warm glow behind the logo.
        RectangleGeometry unit = new RectangleGeometry(new Rect(0, 0, 1, 1));
        LinearGradientBrush baseFill = light
            ? new LinearGradientBrush(Hex("#FFFFFF"), Hex("#F1F0EC"), 90)
            : new LinearGradientBrush(Hex("#26272C"), Hex("#141518"), 90);
        RadialGradientBrush warmth = new RadialGradientBrush(Hex(light ? "#26D97757" : "#38D97757"), Hex("#00D97757"));
        warmth.Center = new Point(0.06, 0);
        warmth.GradientOrigin = new Point(0.06, 0);
        warmth.RadiusX = 0.85;
        warmth.RadiusY = 0.7;
        DrawingGroup layers = new DrawingGroup();
        layers.Children.Add(new GeometryDrawing(baseFill, null, unit));
        layers.Children.Add(new GeometryDrawing(warmth, null, unit));
        DrawingBrush fill = new DrawingBrush(layers);
        fill.Stretch = Stretch.Fill;
        fill.Freeze();
        return fill;
    }

    static Brush PillFill(bool light)
    {
        return light ? Gradient("#FFFFFF", "#ECEBE7") : Gradient("#2A2B30", "#17181B");
    }

    static bool WindowsUsesLightTheme()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value is int && (int)value == 1;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    void ApplyTheme()
    {
        bool light = themeSetting == "light" || (themeSetting != "dark" && WindowsUsesLightTheme());
        if (lightApplied == light) return;
        lightApplied = light;

        Text.Color = Hex(light ? "#1C1C1E" : "#F2F2F0");
        Muted.Color = Hex(light ? "#66665F" : "#A3A3A0");
        Track.Color = Hex(light ? "#DEDDD8" : "#3A3B3F");
        Good.Color = Hex(light ? "#2B8A57" : "#6FBF8B");
        Warn.Color = Hex(light ? "#B26F12" : "#E5B454");
        Danger.Color = Hex(light ? "#CF3A36" : "#EF6F6C");
        OverlaySoft.Color = Hex(light ? "#12000000" : "#1FFFFFFF");
        OverlayMid.Color = Hex(light ? "#17000000" : "#26FFFFFF");
        OverlayTrack.Color = Hex(light ? "#1C000000" : "#2EFFFFFF");
        TickBrush.Color = Hex(light ? "#B3000000" : "#D9FFFFFF");
        CardEdge.Color = Hex(light ? "#D6D5D0" : "#3D3F45");
        PillEdge.Color = Hex(light ? "#CFCEC9" : "#44464C");
        PillHover.Color = Hex(light ? "#A09F99" : "#73757D");
        BadgeEdge.Color = Hex(light ? "#FFFFFF" : "#17181B");

        // Application resources freeze a brush once it's used, so the menu gets new brushes each time.
        ResourceDictionary resources = Application.Current.Resources;
        resources["MenuBackground"] = Live(light ? "#FFFFFF" : "#232428");
        resources["MenuEdge"] = Live(light ? "#D6D5D0" : "#44464C");
        resources["MenuHover"] = Live(light ? "#12000000" : "#1FFFFFFF");
        resources["MenuText"] = Live(light ? "#1C1C1E" : "#F2F2F0");
        resources["MenuMuted"] = Live(light ? "#66665F" : "#A3A3A0");
        heroGlow.Opacity = light ? 0.18 : 0.35;

        cardBorder.Background = CardFill(light);
        miniPill.Background = PillFill(light);
        ApplyAccent();
    }

    void ApplyAccent()
    {
        bool light = lightApplied == true;
        string hex = "";
        for (int i = 0; i < Accents.GetLength(0); i++)
        {
            if (Accents[i, 0] == accentName) hex = Accents[i, 2];
        }

        if (hex.Length == 0)
        {
            arcNormal = light ? Gradient("#3A3B40", "#111113") : Gradient("#FFFFFF", "#BDBBB5");
            glowNormal = light ? Hex("#000000") : Hex("#FFFFFF");
        }
        else
        {
            System.Windows.Media.Color accent = Hex(hex);
            System.Windows.Media.Color lifted = System.Windows.Media.Color.FromRgb(
                (byte)(accent.R + (255 - accent.R) * 0.35),
                (byte)(accent.G + (255 - accent.G) * 0.35),
                (byte)(accent.B + (255 - accent.B) * 0.35));
            LinearGradientBrush brush = new LinearGradientBrush(lifted, accent, 90);
            brush.Freeze();
            arcNormal = brush;
            glowNormal = accent;
        }

        foreach (object entry in accentItem.Items)
        {
            MenuItem item = (MenuItem)entry;
            item.IsChecked = (string)item.Tag == accentName;
        }
    }

    void BuildAccentMenu()
    {
        accentItem.Header = "Ring color";
        for (int i = 0; i < Accents.GetLength(0); i++)
        {
            string key = Accents[i, 0];
            MenuItem item = new MenuItem();
            item.Header = Accents[i, 1];
            item.Tag = key;
            item.IsCheckable = true;

            Shapes.Ellipse swatch = new Shapes.Ellipse();
            swatch.Width = 10;
            swatch.Height = 10;
            swatch.Fill = Accents[i, 2].Length == 0 ? (Brush)Text : Color(Accents[i, 2]);
            item.Icon = swatch;

            item.Click += delegate
            {
                accentName = key;
                ApplyAccent();
                SaveSettings();
                Refresh();
            };
            accentItem.Items.Add(item);
        }
    }

    static void Animate(UIElement element, double fromScale, double toScale, double fromOpacity, double toOpacity, int milliseconds, Action done)
    {
        Duration duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        CubicEase ease = new CubicEase();
        ease.EasingMode = EasingMode.EaseOut;

        ScaleTransform scale = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(1, 0);
        element.RenderTransform = scale;
        element.Opacity = fromOpacity;

        DoubleAnimation grow = new DoubleAnimation(fromScale, toScale, duration);
        grow.EasingFunction = ease;
        DoubleAnimation fade = new DoubleAnimation(fromOpacity, toOpacity, duration);
        fade.EasingFunction = ease;
        fade.Completed += delegate
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            done();
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    void Collapse(bool animate = true)
    {
        if (collapsed || switching) return;
        if (!animate)
        {
            ShowMini();
            return;
        }

        switching = true;
        Animate(card, 1, 0.86, 1, 0, 130, delegate
        {
            ShowMini();
            Animate(miniIcon, 0.7, 1, 0, 1, 170, delegate { switching = false; });
        });
    }

    void Expand()
    {
        if (!collapsed || switching) return;
        switching = true;
        Animate(miniIcon, 1, 0.8, 1, 0, 90, delegate
        {
            ShowCard();
            Animate(card, 0.86, 1, 0, 1, 190, delegate { switching = false; });
        });
    }

    void ShowMini()
    {
        double right = Left + Width;
        collapsed = true;
        Content = miniIcon;
        Width = MiniWidth;
        Left = right - MiniWidth;
        SaveSettings();
    }

    void ShowCard()
    {
        double right = Left + Width;
        collapsed = false;
        Content = card;
        Width = FullWidth;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, right - FullWidth);
        SaveSettings();

        // The full card is taller, so keep its bottom edge on screen once it has been measured.
        Dispatcher.BeginInvoke(new Action(delegate
        {
            double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (Top + ActualHeight > bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
        }), DispatcherPriority.Loaded);
    }

    void ApplyThemeAndRefresh()
    {
        ApplyTheme();
        Refresh();
    }

    void RefreshNow()
    {
        accountStamp = DateTime.MinValue;
        Refresh();
        FetchUsage(true);
    }

    async void FetchUsage(bool manual)
    {
        if (usageUrl == null || fetching) return;

        DateTime now = DateTime.UtcNow;
        if (now < blockedUntil) return;
        if (manual && now - lastAttempt < manualGap) return;

        try
        {
            // After a rejected token, wait until Claude Code has written a new one.
            if (rejectedStamp != DateTime.MinValue && File.GetLastWriteTimeUtc(credentialsFile) == rejectedStamp) return;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        RefreshAccount();
        if (!accountKnown || account.Length == 0) return;

        fetching = true;
        lastAttempt = now;
        string forAccount = account;
        string url = usageUrl;
        string credentials = credentialsFile;
        string target = usageFile;
        FetchResult result = await Task.Run(() => UsageClient.Fetch(credentials, url, forAccount, target));
        fetching = false;
        ApplyResult(result);
        Refresh();
    }

    void ApplyResult(FetchResult result)
    {
        switch (result.Status)
        {
            case FetchStatus.Ok:
                backoff = TimeSpan.FromMinutes(10);
                rejectedStamp = DateTime.MinValue;
                checkNote.Text = "";
                break;
            case FetchStatus.RateLimited:
                TimeSpan wait = result.RetryAfterSeconds > 0
                    ? TimeSpan.FromSeconds(Math.Min(result.RetryAfterSeconds, 21600))
                    : backoff;
                blockedUntil = DateTime.UtcNow + wait;
                backoff = TimeSpan.FromMinutes(Math.Min(backoff.TotalMinutes * 2, 60));
                checkNote.Text = "Usage checks are paused for a while. They restart automatically.";
                break;
            case FetchStatus.TokenRejected:
                try { rejectedStamp = File.GetLastWriteTimeUtc(credentialsFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                checkNote.Text = "Waiting for Claude Code to renew its login.";
                break;
            case FetchStatus.TokenExpired:
                checkNote.Text = "Waiting for Claude Code to renew its login.";
                break;
            default:
                checkNote.Text = "Couldn't check usage. It will try again soon.";
                break;
        }
        checkNote.Visibility = checkNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    UsageRow BuildRow(string label)
    {
        UsageRow row = new UsageRow();

        TextBlock name = new TextBlock();
        name.Text = label;
        name.Foreground = Text;
        name.FontSize = 12;

        row.Percent.Foreground = Text;
        row.Percent.FontSize = 12;
        row.Percent.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(row.Percent, Dock.Right);

        DockPanel top = new DockPanel();
        top.Children.Add(row.Percent);
        top.Children.Add(name);

        row.Fill.ColumnDefinitions.Add(row.Used);
        row.Fill.ColumnDefinitions.Add(row.Free);
        row.Bar.CornerRadius = new CornerRadius(2);
        row.Fill.Children.Add(row.Bar);

        row.Track.Height = 4;
        row.Track.CornerRadius = new CornerRadius(2);
        row.Track.Background = Track;
        row.Track.Margin = new Thickness(0, 5, 0, 4);
        row.Track.Child = row.Fill;

        row.Reset.Foreground = Muted;
        row.Reset.FontSize = 11;

        row.Panel.Margin = new Thickness(0, 0, 0, 12);
        row.Panel.Children.Add(top);
        row.Panel.Children.Add(row.Track);
        row.Panel.Children.Add(row.Reset);
        return row;
    }

    ContextMenu BuildMenu()
    {
        topmostItem.Header = "Always on top";
        topmostItem.IsCheckable = true;
        topmostItem.Click += delegate
        {
            Topmost = topmostItem.IsChecked;
            SaveSettings();
        };

        startupItem.Header = "Start with Windows";
        startupItem.IsCheckable = true;
        startupItem.IsChecked = IsStartupEnabled();
        startupItem.Click += delegate { SetStartup(startupItem.IsChecked); };

        MenuItem refresh = new MenuItem();
        refresh.Header = "Refresh now";
        refresh.Click += delegate { RefreshNow(); };

        MenuItem exit = new MenuItem();
        exit.Header = "Exit";
        exit.Click += delegate { Close(); };

        BuildAccentMenu();

        ContextMenu menu = new ContextMenu();
        menu.Items.Add(topmostItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(accentItem);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    static string ReadShared(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    void RefreshPlan()
    {
        if (credentialsFile != null)
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(credentialsFile);
                if (stamp != planStamp)
                {
                    credentialsPlan = UsageClient.ReadPlan(credentialsFile);
                    planStamp = stamp;
                }
            }
            catch (Exception)
            {
                // Keeps the last label; the file may be mid-write. It's retried on the next refresh.
            }
        }

        string plan = accountPlan ?? credentialsPlan;
        bool signedIn = accountKnown && account.Length > 0;
        planText.Text = plan ?? "";
        planChip.Visibility = signedIn && plan != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void Refresh()
    {
        RefreshAccount();
        RefreshPlan();
        RefreshUsage();
    }

    void RefreshAccount()
    {
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(accountFile);
            if (stamp != accountStamp)
            {
                string text = ReadShared(accountFile);
                Match match = Regex.Match(text, "\"oauthAccount\"\\s*:\\s*\\{[^{}]*?\"emailAddress\"\\s*:\\s*\"([^\"]+)\"");
                account = match.Success ? match.Groups[1].Value : "";

                // The name Claude itself displays. The login file can hold an older internal name.
                Match planMatch = Regex.Match(text, "\"oauthAccount\"\\s*:\\s*\\{.*?\"planDisplayName\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Singleline);
                accountPlan = match.Success && planMatch.Success ? planMatch.Groups[1].Value : null;
                accountKnown = true;
                accountStamp = stamp;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!accountKnown)
        {
            ShowAccount("Account unavailable", "Couldn't read the Claude Code account file", Warn);
        }
        else if (account.Length == 0)
        {
            ShowAccount("Not signed in", "Claude Code has no account signed in", Danger);
        }
        else if (expectedAccount.Length == 0 || string.Equals(account, expectedAccount, StringComparison.OrdinalIgnoreCase))
        {
            ShowAccount(account, "Signed in to Claude Code", Good);
        }
        else
        {
            ShowAccount(account, "Warning: not your expected account", Danger);
        }
    }

    void ShowAccount(string title, string note, Brush color)
    {
        email.Text = title;
        email.ToolTip = title;
        accountNote.Text = note;
        accountNote.Foreground = color == Good ? Muted : color;
        dot.Background = color;
        if (cardBorder != null) cardBorder.BorderBrush = color == Good ? cardEdge : color;
        miniBadge.Background = color;
        miniBadge.Visibility = color == Good ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshUsage()
    {
        string text = null;
        try
        {
            if (File.Exists(usageFile)) text = ReadShared(usageFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        sevenReset = null;
        bool usable = text != null
            && accountKnown
            && account.Length > 0
            && string.Equals(ReadString(text, "account"), account, StringComparison.OrdinalIgnoreCase);

        bool hasFive = usable && ShowWindow(fiveHour, text, "five_hour");
        bool hasSeven = usable && ShowWindow(sevenDay, text, "seven_day");
        fiveHour.Panel.Visibility = hasFive ? Visibility.Visible : Visibility.Collapsed;
        sevenDay.Panel.Visibility = hasSeven ? Visibility.Visible : Visibility.Collapsed;

        bool sevenKnown = hasSeven && sevenDay.Percent.Text != "--";
        double sevenUsed = 0;
        if (sevenKnown) double.TryParse(sevenDay.Percent.Text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out sevenUsed);

        // Stays neutral while usage is fine, so color only appears when it matters.
        Brush alert = sevenUsed >= 90 ? Danger : sevenUsed >= 70 ? Warn : null;
        miniPercent.Text = sevenKnown ? sevenDay.Percent.Text : "--";
        miniPercent.Foreground = !sevenKnown ? Muted : alert ?? Text;

        heroNumber.Foreground = !sevenKnown ? Muted : alert ?? Text;
        heroNumber.Inlines.Clear();
        if (sevenKnown)
        {
            Run percentSign = new Run("%");
            percentSign.FontSize = 13;
            heroNumber.Inlines.Add(new Run(Math.Round(sevenUsed).ToString(CultureInfo.InvariantCulture)));
            heroNumber.Inlines.Add(percentSign);
        }
        else
        {
            heroNumber.Inlines.Add(new Run("--"));
        }
        heroCaption.Visibility = sevenKnown ? Visibility.Visible : Visibility.Collapsed;

        UpdatePace(sevenKnown, sevenUsed);
        SetGauges(sevenUsed,
            sevenUsed >= 90 ? ArcDanger : sevenUsed >= 70 ? ArcWarn : arcNormal,
            sevenUsed >= 90 ? Hex("#EF6F6C") : sevenUsed >= 70 ? Hex("#E5B454") : glowNormal);
        miniIcon.ToolTip = email.Text + ". 7-day limit: " + (sevenKnown ? sevenDay.Percent.Text : "not available") + ". Click to open";

        if (hasFive || hasSeven)
        {
            emptyNote.Visibility = Visibility.Collapsed;
            divider.Visibility = Visibility.Visible;
            updated.Text = "Updated " + Ago(ReadNumber(text, "updated"));
            updated.Visibility = Visibility.Visible;
        }
        else
        {
            emptyNote.Text = "No usage data for this account yet. It appears after the next check.";
            divider.Visibility = Visibility.Collapsed;
            emptyNote.Visibility = Visibility.Visible;
            updated.Visibility = Visibility.Collapsed;
        }
    }

    bool ShowWindow(UsageRow row, string text, string key)
    {
        Match match = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\\{([^{}]*)\\}");
        if (!match.Success)
        {
            // Anthropic sends no 5-hour figure when the account has none, so the row stays hidden until one arrives.
            if (row == fiveHour) return false;

            SetBar(row, 0, Text);
            row.Track.Visibility = Visibility.Collapsed;
            row.Percent.Text = "--";
            row.Reset.Text = "Not reported right now";
            return true;
        }

        double used = ReadNumber(match.Groups[1].Value, "used");
        double resets = ReadNumber(match.Groups[1].Value, "resets");
        if (double.IsNaN(used)) return false;

        DateTime now = DateTime.UtcNow;
        bool hasReset = !double.IsNaN(resets) && resets > 0;
        DateTime resetAt = hasReset ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(resets) : now;
        if (row == sevenDay && hasReset && resetAt > now) sevenReset = resetAt;

        if (hasReset && resetAt <= now)
        {
            SetBar(row, 0, Text);
            row.Track.Visibility = Visibility.Visible;
            row.Percent.Text = "--";
            row.Reset.Text = "Limit has reset. Waiting for the next check";
            return true;
        }

        double clamped = Math.Max(0, Math.Min(100, used));
        SetBar(row, clamped, clamped >= 90 ? Danger : clamped >= 70 ? Warn : Text);
        row.Track.Visibility = Visibility.Visible;
        row.Percent.Text = Math.Round(clamped).ToString(CultureInfo.InvariantCulture) + "%";
        row.Reset.Text = hasReset ? ResetText(resetAt, now) : "Reset time unknown";
        return true;
    }

    static void SetBar(UsageRow row, double percent, Brush color)
    {
        row.Used.Width = new GridLength(percent, GridUnitType.Star);
        row.Free.Width = new GridLength(100 - percent, GridUnitType.Star);
        row.Bar.Background = color;
    }

    static string ResetText(DateTime resetAt, DateTime now)
    {
        TimeSpan left = resetAt - now;
        if (left.TotalHours >= 24)
        {
            return "Resets " + resetAt.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.InvariantCulture);
        }
        if (left.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "Resets in {0}h {1}m", (int)left.TotalHours, left.Minutes);
        }
        return string.Format(CultureInfo.InvariantCulture, "Resets in {0}m", Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
    }

    static string Ago(double epochMs)
    {
        if (double.IsNaN(epochMs)) return "time unknown";
        DateTime then = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(epochMs);
        TimeSpan age = DateTime.UtcNow - then;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return string.Format(CultureInfo.InvariantCulture, "{0} min ago", (int)age.TotalMinutes);
        if (age.TotalDays < 1) return string.Format(CultureInfo.InvariantCulture, "{0} h ago", (int)age.TotalHours);
        return string.Format(CultureInfo.InvariantCulture, "{0} d ago", (int)age.TotalDays);
    }

    static string ReadString(string json, string key)
    {
        Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    static double ReadNumber(string json, string key)
    {
        Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.eE+-]+)");
        double value;
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }
        return double.NaN;
    }

    void LoadSettings()
    {
        double left = double.NaN;
        double top = double.NaN;
        bool startCollapsed = false;
        try
        {
            string[] parts = File.ReadAllText(settingsFile).Trim().Split(',');
            left = double.Parse(parts[0], CultureInfo.InvariantCulture);
            top = double.Parse(parts[1], CultureInfo.InvariantCulture);
            Topmost = parts[2] == "1";
            startCollapsed = parts.Length > 3 && parts[3] == "1";
            if (parts.Length > 4) accentName = parts[4];
            if (parts.Length > 5) themeSetting = parts[5];
        }
        catch (Exception)
        {
            // First run: stay visible above other windows until the user changes it.
            Topmost = true;
        }

        Rect area = SystemParameters.WorkArea;
        bool onScreen = !double.IsNaN(left)
            && left >= SystemParameters.VirtualScreenLeft
            && top >= SystemParameters.VirtualScreenTop
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

        Left = onScreen ? left : area.Right - Width - 20;
        Top = onScreen ? top : area.Top + 20;
        topmostItem.IsChecked = Topmost;
        if (startCollapsed) Collapse(false);
    }

    void SaveSettings()
    {
        try
        {
            // Always stores the full card's position, so it reopens in the same place.
            double savedLeft = collapsed ? Left + Width - FullWidth : Left;
            File.WriteAllText(settingsFile, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}", savedLeft, Top, Topmost ? "1" : "0", collapsed ? "1" : "0", accentName, themeSetting));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool IsStartupEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            return key != null && key.GetValue(RunName) != null;
        }
    }

    static void SetStartup(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled)
            {
                key.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
            }
            else
            {
                key.DeleteValue(RunName, false);
            }
        }
    }
}
