using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Brembana;

public sealed class BridgeApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private string _baseUrl = "";
    private string _username = "";
    private string _password = "";

    public bool IsLoggedIn { get; private set; }
    public string Username => _username;
    public BridgeUserDto? CurrentUser { get; private set; }

    public BridgeApiClient()
    {
        _cookies = new CookieContainer();
        // Senza redirect automatici: a sessione scaduta Django risponde 302 verso la
        // pagina di login, e il 302 va visto com'è, non seguito fino a una pagina HTML.
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Risposta che Django dà a una sessione scaduta o a un utente non autorizzato.</summary>
    public static bool IsSessionLost(int statusCode) => statusCode is 302 or 401 or 403;

    public void Configure(string baseUrl, string username, string password)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
    }

    public void ClearSession()
    {
        IsLoggedIn = false;
        CurrentUser = null;
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new BridgeApiException("Bridge base URL not configured.");

        var body = JsonConvert.SerializeObject(new { username = _username, password = _password });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/bridge/auth/login/", content, cancellationToken);
        CurrentUser = await EnsureOkAsync<BridgeUserDto>(response, cancellationToken);
        IsLoggedIn = true;
    }

    public async Task<BridgeUserDto> GetAuthMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/bridge/auth/me/", cancellationToken);
        CurrentUser = await EnsureOkAsync<BridgeUserDto>(response, cancellationToken)
            ?? throw new BridgeApiException("Profilo utente non disponibile.");
        IsLoggedIn = true;
        return CurrentUser;
    }

    public async Task<List<AttivitaDto>> GetAttivitaListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/bridge/attivita/", cancellationToken);
        return (await EnsureOkAsync<List<AttivitaDto>>(response, cancellationToken)) ?? new List<AttivitaDto>();
    }

    public async Task<DatasetDto> GetDatasetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/bridge/dataset/", cancellationToken);
        return (await EnsureOkAsync<DatasetDto>(response, cancellationToken)) ?? new DatasetDto();
    }

    public async Task UpdateAttivitaAsync(int ormId, AttivitaUpdateRequest update, CancellationToken cancellationToken = default)
    {
        var body = JsonConvert.SerializeObject(update);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/bridge/attivita/{ormId}/update/")
        {
            Content = content
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureOkAsync<object>(response, cancellationToken);
    }

    /// <summary>
    /// Verifica un componente scansionato per un'attività e, con <c>conferma</c>, applica
    /// la sostituzione sulla pianificazione. I rifiuti arrivano come
    /// <see cref="BridgeApiException"/> con <see cref="BridgeApiException.Motivo"/>.
    /// </summary>
    public async Task<AttivitaScanResult> ScanAttivitaResourceAsync(
        int ormId,
        AttivitaScanRequest scan,
        CancellationToken cancellationToken = default)
    {
        var body = JsonConvert.SerializeObject(scan);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/bridge/attivita/{ormId}/scan/", content, cancellationToken);
        return await EnsureOkAsync<AttivitaScanResult>(response, cancellationToken)
            ?? throw new BridgeApiException("Risposta di scansione vuota.");
    }

    public async Task<string> CreateInventoryResourceAsync(
        string table,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken = default)
    {
        var json = JsonConvert.SerializeObject(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/bridge/inventario/{table}/", content, cancellationToken);
        return await EnsureInventoryMutationOkAsync(response, cancellationToken);
    }

    public async Task<string> PatchInventoryResourceAsync(
        string table,
        string uid,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken = default)
    {
        var json = JsonConvert.SerializeObject(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"{_baseUrl}/api/bridge/inventario/{table}/{Uri.EscapeDataString(uid)}/")
        {
            Content = content
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        return await EnsureInventoryMutationOkAsync(response, cancellationToken);
    }

    private static async Task<string> EnsureInventoryMutationOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        JObject? envelope = null;
        try
        {
            envelope = JObject.Parse(json);
        }
        catch
        {
            // non-JSON
        }

        if (response.IsSuccessStatusCode && envelope?["ok"]?.Value<bool>() == true)
        {
            var uid = envelope["uid"]?.ToString() ?? envelope["data"]?["uid"]?.ToString();
            if (!string.IsNullOrWhiteSpace(uid))
                return uid;
            return "";
        }

        var message = envelope?["error"]?.ToString();
        if (string.IsNullOrWhiteSpace(message))
            message = string.IsNullOrWhiteSpace(json) ? $"HTTP {(int)response.StatusCode}" : json;

        throw new BridgeApiException(message, (int)response.StatusCode);
    }

    private async Task<T?> EnsureOkAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        BridgeResponse<T>? envelope = null;
        try
        {
            envelope = JsonConvert.DeserializeObject<BridgeResponse<T>>(json);
        }
        catch
        {
            // non-bridge response
        }

        if (response.IsSuccessStatusCode && envelope?.Ok == true)
            return envelope.Data;

        var message = envelope?.Error;
        if (string.IsNullOrWhiteSpace(message))
        {
            var statusCode = (int)response.StatusCode;
            if (IsSessionLost(statusCode))
                message = UiStrings.BridgeSessionExpired;
            else
                message = string.IsNullOrWhiteSpace(json) ? $"HTTP {statusCode}" : json;
        }

        throw new BridgeApiException(message, (int)response.StatusCode, envelope?.Motivo);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
