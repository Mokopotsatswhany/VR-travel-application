using Mirror;
using Unity.XR.CoreUtils;
using UnityEngine;

[DisallowMultipleComponent]
public class TourNetworkPlayer : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnDisplayNameChanged))]
    private string displayName = "Visitor";

    private XROrigin xrOrigin;
    private Transform bodyVisual;
    private Transform headVisual;
    private TextMesh nameText;
    private Renderer[] cachedRenderers;

    private void Awake()
    {
        EnsureVisuals();

        if (!isClient && !isServer)
        {
            SetVisualsVisible(false);
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        EnsureVisuals();
        SetVisualsVisible(!isLocalPlayer);
        OnDisplayNameChanged(string.Empty, displayName);
    }

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        CacheRig();
        SetVisualsVisible(false);

        if (NetworkManager.singleton is TourMultiplayerManager manager)
        {
            CmdSetDisplayName(manager.LocalPlayerLabel);
        }
    }

    private void LateUpdate()
    {
        if (!isClient && !isServer)
        {
            return;
        }

        if (isLocalPlayer)
        {
            FollowLocalRig();
        }
        else
        {
            BillboardName();
        }
    }

    [Command]
    private void CmdSetDisplayName(string requestedName)
    {
        displayName = string.IsNullOrWhiteSpace(requestedName)
            ? $"Visitor {connectionToClient.connectionId}"
            : requestedName.Trim();
    }

    private void OnDisplayNameChanged(string oldValue, string newValue)
    {
        if (nameText != null)
        {
            nameText.text = newValue;
        }
    }

    private void FollowLocalRig()
    {
        if (xrOrigin == null)
        {
            CacheRig();
        }

        if (xrOrigin == null || xrOrigin.Camera == null)
        {
            return;
        }

        var cameraTransform = xrOrigin.Camera.transform;
        transform.position = cameraTransform.position;
        transform.rotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
    }

    private void BillboardName()
    {
        if (nameText == null)
        {
            return;
        }

        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }

        nameText.transform.rotation = Quaternion.LookRotation(nameText.transform.position - camera.transform.position);
    }

    private void CacheRig()
    {
        xrOrigin = FindAnyObjectByType<XROrigin>();
    }

    private void EnsureVisuals()
    {
        if (bodyVisual != null && headVisual != null && nameText != null)
        {
            return;
        }

        headVisual = CreatePrimitiveVisual("Head", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.18f, 0.18f, 0.18f), new Color(0.88f, 0.72f, 0.42f));
        bodyVisual = CreatePrimitiveVisual("Body", PrimitiveType.Cylinder, new Vector3(0f, -0.7f, 0f), new Vector3(0.18f, 0.5f, 0.18f), new Color(0.19f, 0.53f, 0.76f));

        var textObject = new GameObject("Name Label");
        textObject.transform.SetParent(transform, false);
        textObject.transform.localPosition = new Vector3(0f, 0.28f, 0f);
        nameText = textObject.AddComponent<TextMesh>();
        nameText.anchor = TextAnchor.MiddleCenter;
        nameText.alignment = TextAlignment.Center;
        nameText.fontSize = 48;
        nameText.characterSize = 0.02f;
        nameText.color = Color.white;
        nameText.text = displayName;

        cachedRenderers = GetComponentsInChildren<Renderer>(true);
    }

    private Transform CreatePrimitiveVisual(string name, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Color color)
    {
        var visual = GameObject.CreatePrimitive(type);
        visual.name = name;
        visual.transform.SetParent(transform, false);
        visual.transform.localPosition = localPosition;
        visual.transform.localScale = localScale;

        var collider = visual.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        var renderer = visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            renderer.material.color = color;
        }

        return visual.transform;
    }

    private void SetVisualsVisible(bool visible)
    {
        if (cachedRenderers == null)
        {
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
        }

        foreach (var renderer in cachedRenderers)
        {
            renderer.enabled = visible;
        }

        if (nameText != null)
        {
            nameText.gameObject.SetActive(visible);
        }
    }
}
