using Newtonsoft.Json;

namespace OpenTaiko;

[Serializable]
internal class CLocalizationData {
	[JsonProperty("strings")]
	private Dictionary<string, string> Strings = new Dictionary<string, string>();

	public CLocalizationData() {
		Strings = new Dictionary<string, string>();
	}

	public string[] GetAllStrings() {
		return Strings.Values.ToArray();
	}

	public IReadOnlyDictionary<string, string> GetAllStringsWithLanguageCodes() {
		return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
			new Dictionary<string, string>(Strings, StringComparer.OrdinalIgnoreCase));
	}

	public string GetString(string defaultsDefault) {
		string _lang = CLangManager.fetchLang();
		if (Strings.ContainsKey(_lang))
			return Strings[_lang];
		else if (Strings.ContainsKey("default"))
			return Strings["default"];
		return defaultsDefault;
	}

	public void SetString(string langcode, string str) {
		Strings[langcode] = str;
	}
}
