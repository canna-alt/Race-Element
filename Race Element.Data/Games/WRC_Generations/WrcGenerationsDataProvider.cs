using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using RaceElement.Data.Common.SimulatorData;
using RaceElement.Data.Common.SimulatorData.LocalCar;
using RaceElement.Data.Games.WRC_Generations.UDP;

namespace RaceElement.Data.Games.WRC_Generations;
internal sealed class WrcGenerationsDataProvider : AbstractSimDataProvider
{
    private Thread? _listenerThread;
    private UdpClient? _udpClient;
    private WRCGenData _latestData;
    private bool _isRunning;
    private const int Port = 20777;

    public override List<string> GetCarClasses() => ["WRC", "WRC2", "Legends"];

    public override bool HasTelemetry() => true;

    public override void Update(ref LocalCarData localCar, ref SessionData sessionData, ref GameData gameData)
    {
        var data = _latestData;
        if (data.PositionZ == 0)
        {
            localCar = new();
            sessionData = new();
            gameData.IsGamePaused = true;
            return;
        }
        else
        {
            gameData.IsGamePaused = false;
        }

        // Map to LocalCarData (assuming standard fields; adjust based on exact definitions)
        localCar.Physics.Location = new(data.PositionX, data.PositionY, data.PositionZ);

        localCar.Physics.Velocity = data.Speed * 3.6f;

        localCar.Physics.Acceleration = new(-data.LateralGForce / 9.81f, 0, -data.LongitudinalGForce / 9.81f);

        localCar.Inputs.Throttle = data.Throttle;
        localCar.Inputs.Brake = data.Brake;
        localCar.Inputs.Clutch = data.Clutch;
        localCar.Inputs.Steering = data.SteerAngle;
        localCar.Inputs.Gear = (int)data.CurrentGear + 1;

        localCar.Engine.Rpm = (int)(data.EngineRpms * 10f);
        localCar.Engine.MaxRpm = (int)(data.MaxRpms * 10f);
        localCar.Engine.IsRunning = localCar.Engine.Rpm > 0;

        localCar.Tyres.SlipRatio = [SlipCalc.Ratio(data.WheelSpeedFrontLeft, data.Speed),
                                    SlipCalc.Ratio(data.WheelSpeedFrontRight, data.Speed),
                                    SlipCalc.Ratio(data.WheelSpeedRearLeft, data.Speed),
                                    SlipCalc.Ratio(data.WheelSpeedRearRight, data.Speed)];

        localCar.Tyres.Pressure = [data.WheelPressure0,
                                   data.WheelPressure1,
                                   data.WheelPressure2,
                                   data.WheelPressure3];

        localCar.Tyres.Velocity = [data.WheelSpeedFrontLeft,
                                   data.WheelSpeedFrontRight,
                                   data.WheelSpeedRearLeft,
                                   data.WheelSpeedRearRight];

        localCar.Suspension.RideHeight = [data.SuspensionPositionFrontLeft,
                                          data.SuspensionPositionFrontRight,
                                          data.SuspensionPositionRearLeft,
                                          data.SuspensionPositionRearRight];

        localCar.Brakes.DiscTemperature = [data.BrakeTemp0,
                                           data.BrakeTemp1,
                                           data.BrakeTemp2,
                                           data.BrakeTemp3];

        var forwardVec = new Vector3(data.LocalForwardX, data.LocalForwardY, data.LocalForwardZ);
        var rightVec = new Vector3(data.LocalRightX, data.LocalRightY, data.LocalRightZ);
        var upVec = Vector3.Cross(rightVec, forwardVec);
        localCar.Physics.Rotation = QuaternionFromBasis(rightVec, upVec, forwardVec);

        localCar.Timing.CurrentLaptimeMS = (int)(data.CurrentLapTime * 1000);
        localCar.Race.LapPositionPercentage = data.CurrentLapDistance / data.TrackSize;
    }

    internal override int PollingRate()
    {
        return 60; // 60 Hz
    }

    internal override void Start()
    {
        _isRunning = true;
        _listenerThread = new Thread(ListenForTelemetry)
        {
            IsBackground = true
        };
        _listenerThread.Start();
    }

    internal override void Stop()
    {
        _isRunning = false;
        _udpClient?.Close();
        _listenerThread?.Join(1000);
    }

    private void ListenForTelemetry()
    {
        try
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    var receivedBytes = _udpClient.Receive(ref remoteEndPoint);
                    if (receivedBytes.Length == Marshal.SizeOf<WRCGenData>())
                    {
                        var handle = GCHandle.Alloc(receivedBytes, GCHandleType.Pinned);
                        try
                        {
                            _latestData = Marshal.PtrToStructure<WRCGenData>(handle.AddrOfPinnedObject());
                        }
                        finally
                        {
                            handle.Free();
                        }
                    }
                }
                catch (SocketException) when (!_isRunning)
                {
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _udpClient?.Close();
        }
    }

    private static Quaternion QuaternionFromBasis(Vector3 right, Vector3 up, Vector3 forward)
    {
        var m11 = right.X;
        var m12 = up.X;
        var m13 = forward.X;
        var m21 = right.Y;
        var m22 = up.Y;
        var m23 = forward.Y;
        var m31 = right.Z;
        var m32 = up.Z;
        var m33 = forward.Z;

        float trace = m11 + m22 + m33;
        Quaternion q = new();

        if (trace > 0.0f)
        {
            float s = MathF.Sqrt(trace + 1.0f) * 2.0f;
            q.W = 0.25f * s;
            q.X = (m32 - m23) / s;
            q.Y = (m13 - m31) / s;
            q.Z = (m21 - m12) / s;
        }
        else if (m11 > m22 && m11 > m33)
        {
            float s = MathF.Sqrt(1.0f + m11 - m22 - m33) * 2.0f;
            q.W = (m32 - m23) / s;
            q.X = 0.25f * s;
            q.Y = (m21 + m12) / s;
            q.Z = (m13 + m31) / s;
        }
        else if (m22 > m33)
        {
            float s = MathF.Sqrt(1.0f + m22 - m11 - m33) * 2.0f;
            q.W = (m13 - m31) / s;
            q.X = (m21 + m12) / s;
            q.Y = 0.25f * s;
            q.Z = (m32 + m23) / s;
        }
        else
        {
            float s = MathF.Sqrt(1.0f + m33 - m11 - m22) * 2.0f;
            q.W = (m21 - m12) / s;
            q.X = (m13 + m31) / s;
            q.Y = (m32 + m23) / s;
            q.Z = 0.25f * s;
        }

        return q;
    }
}


/// <summary>
/// Extension methods for calculating wheel slip from WRCGenData.
/// </summary>
internal static class SlipCalc
{
    public static float Ratio(float tyreVelocity, float carVelocity)
    {
        if (carVelocity < 1) return 0;
        float ratio = (tyreVelocity - carVelocity) / tyreVelocity;

        if (ratio < 0) ratio *= -1;

        return ratio;
    }


    private const float Epsilon = 0.0001f;  // Threshold for standstill (m/s)

    /// <summary>
    /// Calculates simplified longitudinal slip ratios using scalar wheel speeds vs. vehicle speed.
    /// Slip ratio formula: |v_wheel - v_speed| / v_speed (positive-only magnitude).
    /// Returns [FrontLeft, FrontRight, RearLeft, RearRight]. Returns 0 for low-speed cases.
    /// Assumes wheel speeds and Speed in m/s; ignores direction/steering for simplicity.
    /// Guaranteed: All values >= 0 (no sub-zero outputs).
    /// </summary>
    /// <param name="data">The WRCGenData packet.</param>
    /// <returns>Array of four slip ratios (dimensionless, non-negative).</returns>
    public static float[] CalculateLongitudinalSlips(this WRCGenData data)
    {
        float v_speed = data.Speed;
        if (v_speed < Epsilon)
        {
            return [0f, 0f, 0f, 0f];  // Standstill: no slip
        }

        float slip_fl = Math.Abs(data.WheelSpeedFrontLeft - v_speed) / v_speed;
        float slip_fr = Math.Abs(data.WheelSpeedFrontRight - v_speed) / v_speed;
        float slip_rl = Math.Abs(data.WheelSpeedRearLeft - v_speed) / v_speed;
        float slip_rr = Math.Abs(data.WheelSpeedRearRight - v_speed) / v_speed;

        //#if DEBUG
        //        // Safety assertion: Ensure no sub-zero values (for dev/testing)
        //        System.Diagnostics.Debug.Assert(slip_fl >= 0f && slip_fr >= 0f && slip_rl >= 0f && slip_rr >= 0f, "Slip values must be non-negative");
        //#endif

        return new[] { slip_fl * 1000f, slip_fr * 1000f, slip_rl * 1000f, slip_rr * 1000f };
    }

    /// <summary>
    /// Calculates longitudinal slip ratios for all four wheels.
    /// Returns: [FrontLeft, FrontRight, RearLeft, RearRight]
    /// </summary>
    public static float[] GetWheelSlipRatios(this WRCGenData data)
    {
        // Compute vehicle longitudinal velocity (dot product of velocity and local forward vectors)
        Vector3 velocity = new(data.VelocityX, data.VelocityY, data.VelocityZ);
        Vector3 localForward = new(data.LocalForwardX, data.LocalForwardY, data.LocalForwardZ);
        float vLong = Vector3.Dot(velocity, localForward);

        float absVLong = Math.Abs(vLong) * 10f;
        if (absVLong < 0.01f)
        {
            // Near-stationary: no meaningful slip
            return [0f, 0f, 0f, 0f];
        }

        // Slip ratios: (wheel_speed - vLong) / |vLong|
        float fl = (data.WheelSpeedFrontLeft * 10f - vLong) / absVLong;
        float fr = (data.WheelSpeedFrontRight - vLong) / absVLong;
        float rl = (data.WheelSpeedRearLeft - vLong) / absVLong;
        float rr = (data.WheelSpeedRearRight - vLong) / absVLong;

        if (fl < 0) fl *= -1;
        if (fr < 0) fr *= -1;
        if (rl < 0) rl *= -1;
        if (rr < 0) rr *= -1;

        return [fl, fr, rl, rr];
    }

}
