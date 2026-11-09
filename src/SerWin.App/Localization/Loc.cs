using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace SerWin.App.Localization;

/// <summary>
/// Accès aux chaînes d'interface (.resx). Le français est la langue neutre (Strings.resx),
/// l'anglais est dans Strings.en.resx. Le changement de langue est immédiat (pas de redémarrage).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private readonly ResourceManager _rm = new("SerWin.App.Resources.Strings", typeof(Loc).Assembly);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr");

    /// <summary>« fr » ou « en » : code utilisé aussi pour les fiches de la base.</summary>
    public string Language { get; private set; } = "fr";

    public string this[string key] => _rm.GetString(key, Culture) ?? $"⟦{key}⟧";

    /// <summary>Chaîne si la clé existe, sinon null (clés construites dynamiquement).</summary>
    public string? TryGet(string key) => _rm.GetString(key, Culture);

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    /// <summary>Langue suivant Windows : français si l'UI de Windows est en français, anglais sinon.</summary>
    public static string SystemLanguage => SystemCulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";

    /// <param name="language">« fr », « en » ou null pour suivre Windows.</param>
    public void SetLanguage(string? language)
    {
        Language = language is "fr" or "en" ? language : SystemLanguage;
        Culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Extension XAML : Text="{loc:T Nav_Services}" — se met à jour au changement de langue.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
