using System.Net.Http.Json;

namespace GoatTicket.IntegrationTests;

/// <summary>
/// Keeps the response body when an API call fails. A bare "500 (Internal Server Error)" says nothing
/// about the cause, so the failure message carries the method, the address, the status and the body.
/// </summary>
internal static class HttpResponseDiagnostics
{
    public static async Task<string> DescribeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var request = response.RequestMessage;
        return $"{request?.Method} {request?.RequestUri?.PathAndQuery} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}";
    }

    public static async Task<T> GetJsonOrFailAsync<T>(this HttpClient client, string requestUri)
    {
        var response = await client.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(await response.DescribeAsync());
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
