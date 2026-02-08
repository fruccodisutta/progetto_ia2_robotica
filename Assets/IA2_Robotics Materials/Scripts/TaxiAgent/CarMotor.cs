using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class CarMotor : MonoBehaviour
{
    [Header("--- Robotica & Cinematica ---")]
    public float wheelBase;
    [Range(10f, 60f)] public float maxSteerAngle;
    public float maxSpeed;
    public float acceleration;
    public float brakePower;
    public float steeringSpeed;

    //Distanza del perno di rotazione (asse posteriore) rispetto al centro dell'oggetto. Solitamente metà del WheelBase.
    public float rearAxleOffset;

    private float currentSpeed = 0f;
    private float currentSteerAngle = 0f;

    public float CurrentSpeed => currentSpeed;
    public float CurrentSteerAngle => currentSteerAngle;

    // ------------
    // Variabili per audio motore - inutile ai fini della simulazione
    [Header("--- Engine Audio ---")]
    [SerializeField] private AudioClip engineLowClip;
    [SerializeField] private AudioClip engineHighClip;
    [SerializeField] [Range(0f, 3f)] private float masterVolume = 1.0f;
    [SerializeField] private Vector2 lowPitchRange = new Vector2(0.85f, 1.1f);
    [SerializeField] private Vector2 highPitchRange = new Vector2(0.95f, 1.35f);
    [SerializeField] private Vector2 volumeRange = new Vector2(0.3f, 1.2f);
    [SerializeField] private float accelerationPitchBoost = 0.04f;
    [SerializeField] private float accelerationVolumeBoost = 0.12f;
    [SerializeField] private float audioSmoothing = 6f;
    [SerializeField] private float audioSmoothingDown = 2f;
    [SerializeField] private float lowIdleVolumeBoost = 0.25f;
    [SerializeField] private float lowIdleSpeedThreshold = 0.25f;
    [SerializeField] private bool engine3D = true;
    [SerializeField] private bool autoStartEngineAudio = true;
    private CarMotorAudio motorAudio;
    // ------------

    private void Awake()
    {
        motorAudio = new CarMotorAudio();
        motorAudio.Configure(
            engineLowClip,
            engineHighClip,
            masterVolume,
            lowPitchRange,
            highPitchRange,
            volumeRange,
            accelerationPitchBoost,
            accelerationVolumeBoost,
            audioSmoothing,
            audioSmoothingDown,
            lowIdleVolumeBoost,
            lowIdleSpeedThreshold,
            engine3D,
            autoStartEngineAudio
        );
        motorAudio.Setup(gameObject, currentSpeed);
    }

    public void SetSteerAngle(float angle)
    {
        currentSteerAngle = angle;
    }

    public void ApplyKinematics(float targetSpeed, bool isReversing)
    {
        // Calcolo velocità con accelerazione/frenata
        float speedChange = (currentSpeed > targetSpeed) ? brakePower : acceleration;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, speedChange * Time.deltaTime);

        if (!isReversing && currentSpeed < 0) currentSpeed = 0;
        
        // Rotazione (applica solo se in movimento)
        if (Mathf.Abs(currentSpeed) > 0.01f)
        {
            float steeringRad = currentSteerAngle * Mathf.Deg2Rad;
            
            // Formula Cinematica (Bicycle Model): omega = v / L * tan(delta)
            float rotationAngleRad = (currentSpeed / wheelBase) * Mathf.Tan(steeringRad) * Time.deltaTime;
            float rotationAngleDeg = rotationAngleRad * Mathf.Rad2Deg;

            // Calcola posizione asse posteriore per rotazione realistica
            Vector3 rotationPivot = transform.position - (transform.forward * rearAxleOffset);
            transform.RotateAround(rotationPivot, Vector3.up, rotationAngleDeg);
        }

        // Movimento lineare
        transform.position += transform.forward * currentSpeed * Time.deltaTime;

        if (motorAudio != null)
        {
            motorAudio.Update(Time.deltaTime, currentSpeed, maxSpeed, acceleration);
        }
    }
}

// Gestisce l'audio del motore in modo separato dalla logica di movimento.
public class CarMotorAudio
{
    // Configurazione audio
    private AudioClip engineLowClip;
    private AudioClip engineHighClip;
    private float masterVolume;
    private Vector2 lowPitchRange;
    private Vector2 highPitchRange;
    private Vector2 volumeRange;
    private float accelerationPitchBoost;
    private float accelerationVolumeBoost;
    private float audioSmoothing;
    private float audioSmoothingDown;
    private float lowIdleVolumeBoost;
    private float lowIdleSpeedThreshold;
    private bool engine3D;
    private bool autoStartEngineAudio;

    // Stato interno audio
    private AudioSource lowSource;
    private AudioSource highSource;
    private float lastSpeed;
    private float smoothedSpeedRatio;

    public void Configure(
        AudioClip lowClip,
        AudioClip highClip,
        float masterVolume,
        Vector2 lowPitchRange,
        Vector2 highPitchRange,
        Vector2 volumeRange,
        float accelerationPitchBoost,
        float accelerationVolumeBoost,
        float audioSmoothing,
        float audioSmoothingDown,
        float lowIdleVolumeBoost,
        float lowIdleSpeedThreshold,
        bool engine3D,
        bool autoStartEngineAudio)
    {
        engineLowClip = lowClip;
        engineHighClip = highClip;
        this.masterVolume = masterVolume;
        this.lowPitchRange = lowPitchRange;
        this.highPitchRange = highPitchRange;
        this.volumeRange = volumeRange;
        this.accelerationPitchBoost = accelerationPitchBoost;
        this.accelerationVolumeBoost = accelerationVolumeBoost;
        this.audioSmoothing = audioSmoothing;
        this.audioSmoothingDown = audioSmoothingDown;
        this.lowIdleVolumeBoost = lowIdleVolumeBoost;
        this.lowIdleSpeedThreshold = lowIdleSpeedThreshold;
        this.engine3D = engine3D;
        this.autoStartEngineAudio = autoStartEngineAudio;
    }

    public void Setup(GameObject owner, float initialSpeed)
    {
        if (owner == null) return;

        lowSource = owner.GetComponent<AudioSource>();
        if (lowSource == null)
        {
            return;
        }

        ConfigureSource(lowSource, engineLowClip);

        if (engineLowClip != null && engineHighClip != null)
        {
            highSource = owner.AddComponent<AudioSource>();
            ConfigureSource(highSource, engineHighClip);
        }
        else
        {
            highSource = null;
        }

        lastSpeed = initialSpeed;
        smoothedSpeedRatio = 0f;

        if (autoStartEngineAudio)
        {
            if (engineLowClip != null && !lowSource.isPlaying) lowSource.Play();
            if (highSource != null && !highSource.isPlaying) highSource.Play();
        }
    }

    public void Update(float deltaTime, float currentSpeed, float maxSpeed, float acceleration)
    {
        if (lowSource == null || engineLowClip == null)
        {
            return;
        }

        if (autoStartEngineAudio && !lowSource.isPlaying) lowSource.Play();
        if (autoStartEngineAudio && highSource != null && !highSource.isPlaying) highSource.Play();

        float rawSpeedRatio = Mathf.Clamp01(Mathf.Abs(currentSpeed) / Mathf.Max(0.01f, maxSpeed));
        float ratioSmoothing = rawSpeedRatio > smoothedSpeedRatio ? audioSmoothing : (audioSmoothingDown > 0f ? audioSmoothingDown : audioSmoothing);
        smoothedSpeedRatio = Mathf.Lerp(smoothedSpeedRatio, rawSpeedRatio, ratioSmoothing * deltaTime);
        float accel = (currentSpeed - lastSpeed) / Mathf.Max(0.0001f, deltaTime);
        float accelRatio = accel > 0f ? Mathf.Clamp01(accel / Mathf.Max(0.01f, acceleration)) : 0f;

        float pitchBoost = accelRatio * accelerationPitchBoost;
        float targetLowPitch = Mathf.Lerp(lowPitchRange.x, lowPitchRange.y, smoothedSpeedRatio) + pitchBoost;
        float targetHighPitch = Mathf.Lerp(highPitchRange.x, highPitchRange.y, smoothedSpeedRatio) + pitchBoost;

        float baseVolume = Mathf.Lerp(volumeRange.x, volumeRange.y, smoothedSpeedRatio);
        float targetVolume = (baseVolume + accelRatio * accelerationVolumeBoost) * masterVolume;

        float smoothUp = audioSmoothing * deltaTime;
        float smoothDown = (audioSmoothingDown > 0f ? audioSmoothingDown : audioSmoothing) * deltaTime;

        if (highSource != null)
        {
            float highMix = smoothedSpeedRatio;
            float lowMix = 1f - highMix;

            float idleBoost = Mathf.Lerp(lowIdleVolumeBoost, 0f, Mathf.InverseLerp(0f, lowIdleSpeedThreshold, smoothedSpeedRatio));
            float lowTargetVolume = (targetVolume * lowMix) + (idleBoost * masterVolume);
            float highTargetVolume = targetVolume * highMix;
            lowSource.volume = SmoothValue(lowSource.volume, lowTargetVolume, smoothUp, smoothDown);
            highSource.volume = SmoothValue(highSource.volume, highTargetVolume, smoothUp, smoothDown);

            lowSource.pitch = SmoothValue(lowSource.pitch, targetLowPitch, smoothUp, smoothDown);
            highSource.pitch = SmoothValue(highSource.pitch, targetHighPitch, smoothUp, smoothDown);
        }
        else
        {
            float idleBoost = Mathf.Lerp(lowIdleVolumeBoost, 0f, Mathf.InverseLerp(0f, lowIdleSpeedThreshold, smoothedSpeedRatio));
            lowSource.volume = SmoothValue(lowSource.volume, targetVolume + (idleBoost * masterVolume), smoothUp, smoothDown);
            lowSource.pitch = SmoothValue(lowSource.pitch, targetLowPitch, smoothUp, smoothDown);
        }

        lastSpeed = currentSpeed;
    }

    private void ConfigureSource(AudioSource source, AudioClip clip)
    {
        source.clip = clip;
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = engine3D ? 1f : 0f;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.volume = 0f;
    }

    private float SmoothValue(float current, float target, float up, float down)
    {
        float smoothing = target > current ? up : down;
        return Mathf.Lerp(current, target, smoothing);
    }
}
