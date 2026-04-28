using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class TourGoogleMapsService : MonoBehaviour
{
    [Serializable]
    private class DirectionsResponse
    {
        public string status;
        public RouteData[] routes;
    }

    [Serializable]
    private class RouteData
    {
        public LegData[] legs;
    }

    [Serializable]
    private class LegData
    {
        public DistanceData distance;
        public DurationData duration;
    }

    [Serializable]
    private class DistanceData
    {
        public string text;
    }

    [Serializable]
    private class DurationData
    {
        public string text;
    }

    [Header("Google Maps Platform")]
    [TextArea(1, 2)]
    public string apiKey = "";

    [Header("Static Map")]
    [Range(3, 18)]
    public int previewZoom = 11;

    [Min(256)]
    public int previewWidth = 640;

    [Min(256)]
    public int previewHeight = 400;

    public string mapType = "terrain";

    [Header("Directions")]
    public string travelMode = "driving";

    private Coroutine previewRoutine;
    private Coroutine directionsRoutine;
    private int previewVersion;
    private int directionsVersion;

    public event Action<Texture2D, string> PreviewUpdated;
    public event Action<string> DirectionsUpdated;

    public bool HasApiKey =>
        !string.IsNullOrWhiteSpace(apiKey) &&
        apiKey.IndexOf("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase) < 0;

    public void RequestPreview(TourSystem.TourStop stop)
    {
        previewVersion++;

        if (!HasApiKey)
        {
            PreviewUpdated?.Invoke(null, "Live map preview needs a Google Maps API key. You can still press M to open the landmark in Google Maps.");
            return;
        }

        if (stop == null || !stop.useRealWorldCoordinates)
        {
            PreviewUpdated?.Invoke(null, "This stop needs GPS coordinates before a live map preview can be loaded.");
            return;
        }

        if (previewRoutine != null)
        {
            StopCoroutine(previewRoutine);
        }

        previewRoutine = StartCoroutine(RequestPreviewRoutine(stop, previewVersion));
    }

    public void RequestDirections(TourSystem.TourStop origin, TourSystem.TourStop destination)
    {
        directionsVersion++;

        if (origin == null || destination == null || !origin.useRealWorldCoordinates || !destination.useRealWorldCoordinates)
        {
            DirectionsUpdated?.Invoke("Route information needs real-world coordinates on both landmarks.");
            return;
        }

        if (origin == destination)
        {
            DirectionsUpdated?.Invoke("You are already at this landmark, so no route leg is needed.");
            return;
        }

        if (!HasApiKey)
        {
            var distance = CalculateDistanceKilometres(origin.coordinates, destination.coordinates);
            DirectionsUpdated?.Invoke($"Approximate straight-line distance: {distance:0.0} km. Add a Google Maps API key for live travel time and road routing.");
            return;
        }

        if (directionsRoutine != null)
        {
            StopCoroutine(directionsRoutine);
        }

        directionsRoutine = StartCoroutine(RequestDirectionsRoutine(origin, destination, directionsVersion));
    }

    private IEnumerator RequestPreviewRoutine(TourSystem.TourStop stop, int version)
    {
        PreviewUpdated?.Invoke(null, "Loading live Google Maps preview...");

        var url = BuildStaticMapUrl(stop.coordinates);
        using (var request = UnityWebRequestTexture.GetTexture(url))
        {
            yield return request.SendWebRequest();

            if (version != previewVersion)
            {
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                PreviewUpdated?.Invoke(null, $"Google Maps preview failed: {request.error}");
                yield break;
            }

            var texture = DownloadHandlerTexture.GetContent(request);
            PreviewUpdated?.Invoke(texture, "Live Google Maps preview loaded.");
        }
    }

    private IEnumerator RequestDirectionsRoutine(TourSystem.TourStop origin, TourSystem.TourStop destination, int version)
    {
        DirectionsUpdated?.Invoke("Loading live route data from Google Maps...");

        var url = BuildDirectionsUrl(origin.coordinates, destination.coordinates);
        using (var request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (version != directionsVersion)
            {
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                DirectionsUpdated?.Invoke($"Google Maps directions failed: {request.error}");
                yield break;
            }

            var response = JsonUtility.FromJson<DirectionsResponse>(request.downloadHandler.text);
            if (response == null || !string.Equals(response.status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                var status = response != null ? response.status : "unknown response";
                DirectionsUpdated?.Invoke($"Google Maps directions did not return a valid route. Status: {status}.");
                yield break;
            }

            if (response.routes == null || response.routes.Length == 0 || response.routes[0].legs == null || response.routes[0].legs.Length == 0)
            {
                DirectionsUpdated?.Invoke("Google Maps returned no route legs for these landmarks.");
                yield break;
            }

            var leg = response.routes[0].legs[0];
            var distance = leg.distance != null ? leg.distance.text : "distance unavailable";
            var duration = leg.duration != null ? leg.duration.text : "duration unavailable";
            DirectionsUpdated?.Invoke($"Live route: {distance} away, about {duration} by {travelMode}.");
        }
    }

    private string BuildStaticMapUrl(Vector2 coordinates)
    {
        var latitude = coordinates.x.ToString("0.000000", CultureInfo.InvariantCulture);
        var longitude = coordinates.y.ToString("0.000000", CultureInfo.InvariantCulture);
        return
            "https://maps.googleapis.com/maps/api/staticmap" +
            $"?center={latitude},{longitude}" +
            $"&zoom={previewZoom}" +
            $"&size={previewWidth}x{previewHeight}" +
            $"&maptype={UnityWebRequest.EscapeURL(mapType)}" +
            $"&markers=color:red%7C{latitude},{longitude}" +
            $"&key={UnityWebRequest.EscapeURL(apiKey)}";
    }

    private string BuildDirectionsUrl(Vector2 origin, Vector2 destination)
    {
        var originLatitude = origin.x.ToString("0.000000", CultureInfo.InvariantCulture);
        var originLongitude = origin.y.ToString("0.000000", CultureInfo.InvariantCulture);
        var destinationLatitude = destination.x.ToString("0.000000", CultureInfo.InvariantCulture);
        var destinationLongitude = destination.y.ToString("0.000000", CultureInfo.InvariantCulture);

        return
            "https://maps.googleapis.com/maps/api/directions/json" +
            $"?origin={originLatitude},{originLongitude}" +
            $"&destination={destinationLatitude},{destinationLongitude}" +
            $"&mode={UnityWebRequest.EscapeURL(travelMode)}" +
            $"&key={UnityWebRequest.EscapeURL(apiKey)}";
    }

    private float CalculateDistanceKilometres(Vector2 first, Vector2 second)
    {
        const float earthRadiusKilometres = 6371f;

        var latitudeDelta = Mathf.Deg2Rad * (second.x - first.x);
        var longitudeDelta = Mathf.Deg2Rad * (second.y - first.y);
        var firstLatitude = Mathf.Deg2Rad * first.x;
        var secondLatitude = Mathf.Deg2Rad * second.x;

        var a =
            Mathf.Sin(latitudeDelta * 0.5f) * Mathf.Sin(latitudeDelta * 0.5f) +
            Mathf.Cos(firstLatitude) * Mathf.Cos(secondLatitude) *
            Mathf.Sin(longitudeDelta * 0.5f) * Mathf.Sin(longitudeDelta * 0.5f);

        var c = 2f * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1f - a));
        return earthRadiusKilometres * c;
    }
}
