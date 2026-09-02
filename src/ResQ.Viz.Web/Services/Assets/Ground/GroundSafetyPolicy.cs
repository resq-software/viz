/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>What an emergency stop does to one ground platform.</summary>
/// <remarks>
/// A <b>policy</b>, deliberately separate from both <see cref="GroundProfile"/> and the executor
/// that applies it. It is not on the profile because the profile is the integrator's contract —
/// wheelbase, braking rate, step height — and a decision about whether to inhibit the drivetrain
/// is not physics; putting it there would make the integrator's inputs depend on an operating
/// rule. It is not hardcoded in the executor either, because "an emergency stop disarms" is
/// exactly the kind of assumption that is right for one fleet and wrong for the next, and a fleet
/// that must retain drive authority — to hold itself on a slope, or to keep a brake energised —
/// needs to be able to say so rather than to patch the executor.
/// <para>
/// <see cref="For"/> is the one place a default is derived, so a platform that has not been given
/// an explicit policy still gets a documented one rather than an accidental one.
/// </para>
/// </remarks>
/// <param name="DisarmOnEmergencyStop">
/// True when an emergency stop also inhibits the drivetrain, so every later command that would
/// produce motion is refused until the stop is explicitly released. False stops the vehicle just
/// as hard but leaves it commandable.
/// </param>
/// <param name="HasServiceBrake">
/// True when the platform can brake rather than merely coast. It decides only what the raised
/// event says, because a zero-speed setpoint is chased at
/// <see cref="GroundProfile.MaxBrakingMps2"/> either way — the motion model has no coast mode to
/// express the difference in. The flag exists so a coasting platform can be described honestly
/// when one arrives, rather than being silently reported as braking.
/// </param>
public readonly record struct GroundSafetyPolicy(bool DisarmOnEmergencyStop, bool HasServiceBrake)
{
    /// <summary>The default policy for a platform, derived from its profile.</summary>
    /// <remarks>
    /// Disarming is the default because every platform modelled here can hold itself at rest
    /// without drive torque: each declares a zero minimum speed and no passive drift, so
    /// inhibiting the drivetrain costs nothing and removes any possibility of a stale setpoint
    /// moving a vehicle an operator believes is stopped. A displacement hull could not make that
    /// trade, which is precisely why the decision is a policy rather than a constant.
    /// </remarks>
    /// <param name="profile">Platform to derive a policy for.</param>
    /// <returns>The default policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static GroundSafetyPolicy For(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new GroundSafetyPolicy(
            DisarmOnEmergencyStop: true,
            HasServiceBrake: profile.MaxBrakingMps2 > 0.0);
    }
}
