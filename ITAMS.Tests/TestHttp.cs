using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ITAMS.Tests;

public static class TestHttp
{
    /// <summary>Same JSON conventions as the API: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"GET {url} returned {(int)res.StatusCode} instead of success.");
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body, mediaType: null, options: Json) });

    /// <summary>Sends exactly this JSON text, so a test can include fields the API does not define (e.g. a forged tenantId).</summary>
    public static Task<HttpResponseMessage> PostRawJsonAsync(this HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
}
