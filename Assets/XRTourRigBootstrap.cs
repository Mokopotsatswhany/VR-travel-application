using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

[DisallowMultipleComponent]
[RequireComponent(typeof(XROrigin))]
public class XRTourRigBootstrap : MonoBehaviour
{
    [Header("Movement")]
    [Min(0.5f)]
    public float moveSpeed = 2.2f;

    [Min(15f)]
    public float turnSpeed = 80f;

    [Header("Character Controller")]
    [Min(0.1f)]
    public float controllerRadius = 0.25f;

    [Min(0.01f)]
    public float controllerSkinWidth = 0.03f;

    [Min(0f)]
    public float stepOffset = 0.35f;

    [Min(0.5f)]
    public float minControllerHeight = 1.1f;

    [Min(1f)]
    public float maxControllerHeight = 2.1f;

    private XROrigin xrOrigin;
    private CharacterController characterController;
    private XRBodyTransformer bodyTransformer;
    private LocomotionMediator locomotionMediator;
    private GravityProvider gravityProvider;
    private ContinuousMoveProvider moveProvider;
    private ContinuousTurnProvider turnProvider;
    private XRCharacterControllerFitter characterControllerFitter;

    private bool initialized;

    private void Start()
    {
        InitializeRig();
    }

    public void InitializeRig()
    {
        if (initialized)
        {
            return;
        }

        xrOrigin = GetComponent<XROrigin>();
        if (xrOrigin == null)
        {
            Debug.LogError("XR locomotion bootstrap requires an XROrigin.", this);
            enabled = false;
            return;
        }

        EnsureCharacterController();
        EnsureLocomotionCore();
        EnsureMoveProvider();
        EnsureTurnProvider();
        EnsureCharacterControllerFitter();

        initialized = true;
    }

    public void SetLocomotionEnabled(bool enabled)
    {
        if (!initialized)
        {
            InitializeRig();
        }

        if (moveProvider != null)
        {
            moveProvider.enabled = enabled;
        }

        if (turnProvider != null)
        {
            turnProvider.enabled = enabled;
        }

        if (characterControllerFitter != null)
        {
            characterControllerFitter.enabled = enabled;
        }
    }

    private void EnsureCharacterController()
    {
        var originObject = xrOrigin.Origin != null ? xrOrigin.Origin : gameObject;

        if (!originObject.TryGetComponent(out characterController))
        {
            characterController = originObject.AddComponent<CharacterController>();
        }

        characterController.radius = controllerRadius;
        characterController.skinWidth = controllerSkinWidth;
        characterController.slopeLimit = 45f;
        characterController.stepOffset = stepOffset;
        characterController.minMoveDistance = 0f;
        characterController.height = Mathf.Clamp(
            characterController.height > 0f ? characterController.height : 1.8f,
            minControllerHeight,
            maxControllerHeight);
        characterController.center = new Vector3(0f, characterController.height * 0.5f, 0f);
    }

    private void EnsureLocomotionCore()
    {
        if (!TryGetComponent(out bodyTransformer))
        {
            bodyTransformer = gameObject.AddComponent<XRBodyTransformer>();
        }

        bodyTransformer.xrOrigin = xrOrigin;
        bodyTransformer.useCharacterControllerIfExists = true;

        if (!TryGetComponent(out locomotionMediator))
        {
            locomotionMediator = gameObject.AddComponent<LocomotionMediator>();
        }

        locomotionMediator.xrOrigin = xrOrigin;

        if (!TryGetComponent(out gravityProvider))
        {
            gravityProvider = gameObject.AddComponent<GravityProvider>();
        }

        gravityProvider.mediator = locomotionMediator;
        gravityProvider.useGravity = true;
        gravityProvider.updateCharacterControllerCenterEachFrame = true;
        gravityProvider.sphereCastLayerMask = Physics.DefaultRaycastLayers;
    }

    private void EnsureMoveProvider()
    {
        if (!TryGetComponent(out moveProvider))
        {
            moveProvider = gameObject.AddComponent<ContinuousMoveProvider>();
        }

        moveProvider.mediator = locomotionMediator;
        moveProvider.gravityProvider = gravityProvider;
        moveProvider.moveSpeed = moveSpeed;
        moveProvider.enableStrafe = true;
        moveProvider.enableFly = false;
        moveProvider.forwardSource = xrOrigin.Camera != null ? xrOrigin.Camera.transform : null;
        moveProvider.leftHandMoveInput = CreateMoveReader();
        moveProvider.rightHandMoveInput = CreateUnusedVector2Reader("Unused Right Move");
        moveProvider.leftHandMoveInput.EnableDirectActionIfModeUsed();
        moveProvider.rightHandMoveInput.EnableDirectActionIfModeUsed();
    }

    private void EnsureTurnProvider()
    {
        if (!TryGetComponent(out turnProvider))
        {
            turnProvider = gameObject.AddComponent<ContinuousTurnProvider>();
        }

        turnProvider.mediator = locomotionMediator;
        turnProvider.turnSpeed = turnSpeed;
        turnProvider.enableTurnLeftRight = true;
        turnProvider.enableTurnAround = false;
        turnProvider.leftHandTurnInput = CreateUnusedVector2Reader("Unused Left Turn");
        turnProvider.rightHandTurnInput = CreateTurnReader();
        turnProvider.leftHandTurnInput.EnableDirectActionIfModeUsed();
        turnProvider.rightHandTurnInput.EnableDirectActionIfModeUsed();
    }

    private void EnsureCharacterControllerFitter()
    {
        var originObject = xrOrigin.Origin != null ? xrOrigin.Origin : gameObject;

        if (!originObject.TryGetComponent(out characterControllerFitter))
        {
            characterControllerFitter = originObject.AddComponent<XRCharacterControllerFitter>();
        }

        characterControllerFitter.xrOrigin = xrOrigin;
        characterControllerFitter.characterController = characterController;
        characterControllerFitter.minHeight = minControllerHeight;
        characterControllerFitter.maxHeight = maxControllerHeight;
    }

    private XRInputValueReader<Vector2> CreateMoveReader()
    {
        var action = new InputAction("Tour Move", InputActionType.Value, expectedControlType: "Vector2");
        action.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}").WithProcessor("StickDeadzone");
        action.AddBinding("<Gamepad>/leftStick").WithProcessor("StickDeadzone");

        var keyboardMovement = action.AddCompositeBinding("2DVector");
        keyboardMovement.With("Up", "<Keyboard>/w");
        keyboardMovement.With("Down", "<Keyboard>/s");
        keyboardMovement.With("Left", "<Keyboard>/a");
        keyboardMovement.With("Right", "<Keyboard>/d");

        return CreateDirectReader("Tour Move", action);
    }

    private XRInputValueReader<Vector2> CreateTurnReader()
    {
        var action = new InputAction("Tour Turn", InputActionType.Value, expectedControlType: "Vector2");
        action.AddBinding("<XRController>{RightHand}/{Primary2DAxis}").WithProcessor("StickDeadzone");
        action.AddBinding("<Gamepad>/rightStick").WithProcessor("StickDeadzone");

        var keyboardTurn = action.AddCompositeBinding("2DVector");
        keyboardTurn.With("Up", "<Keyboard>/period");
        keyboardTurn.With("Down", "<Keyboard>/comma");
        keyboardTurn.With("Left", "<Keyboard>/q");
        keyboardTurn.With("Right", "<Keyboard>/e");

        return CreateDirectReader("Tour Turn", action);
    }

    private XRInputValueReader<Vector2> CreateUnusedVector2Reader(string actionName)
    {
        var reader = new XRInputValueReader<Vector2>(actionName, XRInputValueReader.InputSourceMode.Unused);
        reader.inputSourceMode = XRInputValueReader.InputSourceMode.Unused;
        return reader;
    }

    private XRInputValueReader<Vector2> CreateDirectReader(string actionName, InputAction action)
    {
        var reader = new XRInputValueReader<Vector2>(actionName, XRInputValueReader.InputSourceMode.InputAction);
        reader.inputSourceMode = XRInputValueReader.InputSourceMode.InputAction;
        reader.inputAction = action;
        return reader;
    }
}

public class XRCharacterControllerFitter : MonoBehaviour
{
    public XROrigin xrOrigin;
    public CharacterController characterController;

    [Min(0.5f)]
    public float minHeight = 1.1f;

    [Min(1f)]
    public float maxHeight = 2.1f;

    private void LateUpdate()
    {
        if (xrOrigin == null || characterController == null)
        {
            return;
        }

        var height = Mathf.Clamp(xrOrigin.CameraInOriginSpaceHeight, minHeight, maxHeight);
        var center = xrOrigin.CameraInOriginSpacePos;
        center.y = height * 0.5f + characterController.skinWidth;

        characterController.height = height;
        characterController.center = center;
    }
}
