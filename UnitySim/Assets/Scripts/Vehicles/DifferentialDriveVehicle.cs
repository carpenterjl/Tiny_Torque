using AIHWSim.Actuators;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Two-wheel differential-drive robot built from primitives.
    ///
    /// Physics deliberately avoids WheelCollider: each wheel applies a
    /// longitudinal traction force from tyre slip (wheel surface speed vs.
    /// ground speed) and a lateral friction force, both clamped by a friction
    /// circle. The chassis collider is frictionless and only provides normal
    /// support, so all horizontal dynamics come from this model — which keeps
    /// behaviour predictable and portable to other vehicles later.
    ///
    /// Setpoints: [0] = forward velocity (m/s), [1] = yaw rate (rad/s).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class DifferentialDriveVehicle : MonoBehaviour, IControlledVehicle
    {
        [Header("Geometry")]
        public float wheelRadius = 0.05f;   // m — must match controller config
        public float trackWidth = 0.30f;    // m — distance between wheels
        public float wheelAnchorDepth = 0.05f; // m below chassis centre for force application

        [Header("Traction")]
        [Tooltip("Longitudinal force per unit slip speed (N per m/s).")]
        public float longStiffness = 120f;
        [Tooltip("Lateral force per unit sideways speed (N per m/s).")]
        public float latStiffness = 300f;
        [Tooltip("Tyre friction coefficient (bounds the friction circle).")]
        public float frictionCoeff = 1.2f;

        [Header("Actuators / Sensors")]
        public DcMotorModel leftMotor = new DcMotorModel();
        public DcMotorModel rightMotor = new DcMotorModel();
        public EncoderSensor leftEncoder = new EncoderSensor();
        public EncoderSensor rightEncoder = new EncoderSensor();
        public ImuSensor imu = new ImuSensor();

        private Rigidbody _body;
        private float _cmdLeft, _cmdRight;          // latched, zero-order held
        private float _reactionL, _reactionR;       // load torque fed back to motors
        private Transform _leftWheelViz, _rightWheelViz;

        public int SetpointCount => 2;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>Wire up references (used by the runtime scene builder).</summary>
        public void Configure(Transform leftWheelViz, Transform rightWheelViz)
        {
            _leftWheelViz = leftWheelViz;
            _rightWheelViz = rightWheelViz;
        }

        public void ResetVehicle()
        {
            leftMotor.Reset();
            rightMotor.Reset();
            imu.Reset();
            _cmdLeft = _cmdRight = 0f;
            _reactionL = _reactionR = 0f;
            if (_body != null)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }

        public void SampleSensors(float dt, float[] wheelVel, float[] gyro, float[] accel)
        {
            wheelVel[0] = leftEncoder.Read(leftMotor.AngularVelocity);
            wheelVel[1] = rightEncoder.Read(rightMotor.AngularVelocity);
            wheelVel[2] = 0f;
            wheelVel[3] = 0f;

            imu.Read(_body, dt, out Vector3 g, out Vector3 a);
            gyro[0] = g.x; gyro[1] = g.y; gyro[2] = g.z;
            accel[0] = a.x; accel[1] = a.y; accel[2] = a.z;
        }

        public void SetCommands(float[] actuatorCommands)
        {
            _cmdLeft = actuatorCommands[0];
            _cmdRight = actuatorCommands[1];
        }

        public void StepPhysics(float dt)
        {
            // Advance motors using last step's reaction torque as load.
            leftMotor.Step(_cmdLeft, _reactionL, dt);
            rightMotor.Step(_cmdRight, _reactionR, dt);

            float maxTraction = frictionCoeff * _body.mass * 9.81f * 0.5f; // per wheel

            _reactionL = ApplyWheelForce(-1f, leftMotor.AngularVelocity, maxTraction);
            _reactionR = ApplyWheelForce(+1f, rightMotor.AngularVelocity, maxTraction);

            SpinWheelVisual(_leftWheelViz, leftMotor.AngularVelocity, dt);
            SpinWheelVisual(_rightWheelViz, rightMotor.AngularVelocity, dt);
        }

        /// <summary>
        /// Applies traction for one wheel and returns the reaction torque
        /// magnitude (N·m) to feed back into the motor's load droop.
        /// </summary>
        private float ApplyWheelForce(float side, float wheelOmega, float maxTraction)
        {
            Vector3 localAnchor = new Vector3(side * 0.5f * trackWidth, -wheelAnchorDepth, 0f);
            Vector3 worldPos = transform.TransformPoint(localAnchor);
            Vector3 pointVel = _body.GetPointVelocity(worldPos);

            Vector3 forward = transform.forward;
            Vector3 right = transform.right;

            float vLong = Vector3.Dot(pointVel, forward);
            float vLat = Vector3.Dot(pointVel, right);

            float wheelSurfaceSpeed = wheelOmega * wheelRadius;
            float slip = wheelSurfaceSpeed - vLong;

            float fLong = Mathf.Clamp(longStiffness * slip, -maxTraction, maxTraction);
            float fLat = Mathf.Clamp(-latStiffness * vLat, -maxTraction, maxTraction);

            // Friction circle: scale down if combined force exceeds the limit.
            Vector2 combined = new Vector2(fLong, fLat);
            if (combined.magnitude > maxTraction && combined.magnitude > 1e-4f)
            {
                combined *= maxTraction / combined.magnitude;
                fLong = combined.x;
                fLat = combined.y;
            }

            Vector3 force = forward * fLong + right * fLat;
            _body.AddForceAtPosition(force, worldPos);

            return Mathf.Abs(fLong) * wheelRadius;
        }

        private static void SpinWheelVisual(Transform wheel, float omega, float dt)
        {
            if (wheel == null) return;
            wheel.Rotate(Vector3.right, omega * Mathf.Rad2Deg * dt, Space.Self);
        }

        public void PublishTelemetry(TelemetryHub hub)
        {
            float forwardSpeed = Vector3.Dot(_body.linearVelocity, transform.forward);
            hub.SetValue("veh/speed", forwardSpeed);
            hub.SetValue("veh/yaw_rate", _body.angularVelocity.y);
            hub.SetValue("veh/pos_x", transform.position.x);
            hub.SetValue("veh/pos_z", transform.position.z);
        }
    }
}
