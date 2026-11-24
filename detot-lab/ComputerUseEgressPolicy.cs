using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The final allowlist boundary for data returned by Desktop Companion before it is
/// added to a model request. UIA output is untrusted and new Companion fields are
/// denied by default.
/// </summary>
public static class ComputerUseEgressPolicy
{
    private const int MaximumPayloadCharacters = 32_768;
    private const int MaximumElements = 120;
    private const int MaximumWindows = 32;
    private static readonly HashSet<string> SafeControlTypes = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "ComboBox", "TabItem", "Hyperlink", "MenuItem", "ToolBarButton",
        "CheckBox", "RadioButton", "ToggleButton"
    };

    public static bool TryFilterUiObservation(JsonElement source, out string filteredJson)
    {
        filteredJson = "";
        if (source.ValueKind != JsonValueKind.Object) return false;

        var result = new JsonObject();
        CopyString(source, result, "status", 64);
        CopyString(source, result, "app", 64);
        CopyString(source, result, "operation", 32);
        CopyString(source, result, "windowId", 32);
        CopyString(source, result, "observationId", 32);
        CopyBoolean(source, result, "actionVerified");
        CopyBoolean(source, result, "newWindowVerified");
        CopyBoolean(source, result, "complete");

        if (source.TryGetProperty("windows", out var windows))
        {
            if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > MaximumWindows) return false;
            var safeWindows = new JsonArray();
            foreach (var window in windows.EnumerateArray())
            {
                if (window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("windowId", out var id) || id.ValueKind != JsonValueKind.String)
                    return false;
                var windowId = id.GetString();
                if (windowId is null || windowId.Length != 32 || !Guid.TryParseExact(windowId, "N", out _)) return false;
                safeWindows.Add(new JsonObject { ["windowId"] = windowId });
            }
            result["windows"] = safeWindows;
        }

        if (source.TryGetProperty("elements", out var elements))
        {
            if (!TryFilterElements(elements, out var safeElements)) return false;
            result["elements"] = safeElements;
        }

        if (source.TryGetProperty("observation", out var observation))
        {
            if (observation.ValueKind != JsonValueKind.Object) return false;
            var safeObservation = new JsonObject();
            CopyString(observation, safeObservation, "observationId", 32);
            CopyBoolean(observation, safeObservation, "complete");
            if (observation.TryGetProperty("elements", out var nestedElements))
            {
                if (!TryFilterElements(nestedElements, out var safeElements)) return false;
                safeObservation["elements"] = safeElements;
            }
            result["observation"] = safeObservation;
        }

        filteredJson = result.ToJsonString();
        return filteredJson.Length <= MaximumPayloadCharacters;
    }

    private static bool TryFilterElements(JsonElement elements, out JsonArray filtered)
    {
        filtered = new JsonArray();
        if (elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() > MaximumElements) return false;

        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            var safeElement = new JsonObject();
            if (!element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var elementId = id.GetString();
            if (elementId is null || !System.Text.RegularExpressions.Regex.IsMatch(
                    elementId, "^e[0-9]{1,3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                continue;
            if (!element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
            var controlType = type.GetString();
            if (controlType is null || !SafeControlTypes.Contains(controlType))
                continue;
            safeElement["id"] = elementId;
            safeElement["type"] = controlType;
            if (element.TryGetProperty("name", out var name))
            {
                if (name.ValueKind != JsonValueKind.String) return false;
                var label = name.GetString() ?? "";
                if (label.Length > 120) label = label[..120];
                if (!ComputerUseUiPlanner.IsSensitiveName(label) && !ComputerUseUiPlanner.IsProtectedSurface(label))
                    safeElement["name"] = label;
            }
            CopyBoolean(element, safeElement, "enabled");
            CopyBoolean(element, safeElement, "offscreen");
            CopyBoolean(element, safeElement, "invokable");
            CopyString(element, safeElement, "scrollDirection", 8);
            filtered.Add(safeElement);
        }
        return true;
    }

    private static void CopyString(JsonElement source, JsonObject target, string name, int maximumLength)
    {
        if (!source.TryGetProperty(name, out var value)) return;
        if (value.ValueKind != JsonValueKind.String) return;
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > maximumLength) return;
        target[name] = text;
    }

    private static void CopyBoolean(JsonElement source, JsonObject target, string name)
    {
        if (source.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            target[name] = value.GetBoolean();
    }
}
