using UnityEngine;

// Script per cambiare telecamera
public class CameraManager : MonoBehaviour
{
    public Camera cam1;
    public Camera cam2;
    private bool isCamera1Active = true;

    [Header("Passenger View")]
    public Camera passengerCamera;
    public Transform passengerTarget;
    public Transform taxiTarget;
    public Vector3 passengerCameraOffset = new Vector3(0f, 1.6f, 0f);
    public Vector3 passengerLookOffset = new Vector3(0f, 1.2f, 0f);
    public float passengerFollowSmooth = 6f;
    public bool autoCreatePassengerCamera = true;

    private bool isPassengerViewActive = false;

    void Start()
    {
        if (cam1 != null)
        {
            SetCameraActive(cam1, true);
        }
        if (cam2 != null)
        {
            SetCameraActive(cam2, false);
        }
        if (passengerCamera != null)
        {
            SetCameraActive(passengerCamera, false);
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            if (isPassengerViewActive)
            {
                SwitchToTaxiView();
            }
            else
            {
                SwitchCamera();
            }
        }
    }

    void LateUpdate()
    {
        if (!isPassengerViewActive || passengerCamera == null || passengerTarget == null || taxiTarget == null)
        {
            return;
        }

        Vector3 desiredPos = passengerTarget.position + passengerCameraOffset;
        passengerCamera.transform.position = Vector3.Lerp(
            passengerCamera.transform.position,
            desiredPos,
            passengerFollowSmooth * Time.deltaTime
        );

        Vector3 lookPos = taxiTarget.position + passengerLookOffset;
        Quaternion desiredRot = Quaternion.LookRotation(lookPos - passengerCamera.transform.position, Vector3.up);
        passengerCamera.transform.rotation = Quaternion.Slerp(
            passengerCamera.transform.rotation,
            desiredRot,
            passengerFollowSmooth * Time.deltaTime
        );
    }

    void SwitchCamera()
    {
        isCamera1Active = !isCamera1Active;
        
        if (cam1 != null) SetCameraActive(cam1, isCamera1Active);
        if (cam2 != null) SetCameraActive(cam2, !isCamera1Active);
    }

    public void SwitchToPassengerView(Transform passenger, Transform taxi)
    {
        if (passenger == null || taxi == null) return;

        EnsurePassengerCamera();
        if (passengerCamera == null) return;

        passengerTarget = passenger;
        taxiTarget = taxi;
        isPassengerViewActive = true;

        if (cam1 != null) SetCameraActive(cam1, false);
        if (cam2 != null) SetCameraActive(cam2, false);
        SetCameraActive(passengerCamera, true);

        Vector3 initialPos = passengerTarget.position + passengerCameraOffset;
        passengerCamera.transform.position = initialPos;
        Vector3 lookPos = taxiTarget.position + passengerLookOffset;
        passengerCamera.transform.rotation = Quaternion.LookRotation(lookPos - initialPos, Vector3.up);
    }

    public void SwitchToTaxiView()
    {
        isPassengerViewActive = false;
        passengerTarget = null;
        taxiTarget = null;

        if (passengerCamera != null) SetCameraActive(passengerCamera, false);

        isCamera1Active = true;
        if (cam1 != null) SetCameraActive(cam1, true);
        if (cam2 != null) SetCameraActive(cam2, false);
    }

    public bool IsPassengerViewActive => isPassengerViewActive;

    private void EnsurePassengerCamera()
    {
        if (passengerCamera != null || !autoCreatePassengerCamera) return;

        GameObject camObj = new GameObject("PassengerCamera");
        camObj.transform.SetParent(transform, false);
        passengerCamera = camObj.AddComponent<Camera>();

        if (cam1 != null)
        {
            passengerCamera.CopyFrom(cam1);
        }

        AudioListener listener = camObj.GetComponent<AudioListener>();
        if (listener == null) listener = camObj.AddComponent<AudioListener>();
        listener.enabled = false;
        passengerCamera.enabled = false;
    }

    private void SetCameraActive(Camera cam, bool active)
    {
        cam.enabled = active;
        AudioListener listener = cam.GetComponent<AudioListener>();
        if (listener != null) listener.enabled = active;
    }
}
