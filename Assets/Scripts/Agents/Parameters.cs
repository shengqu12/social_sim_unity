public struct Parameters
{
    // Session 29 STEP 3: raised from 0.6 (walking pace, ~half real human walk speed, per
    // Howard's review + this session's own speed audit -- REPORT.md Session 29). This is the
    // steady-state target SFAgent.UpdateVelocity() computes (see that method's own desiredVel
    // line) -- NOT the actually-measured on-screen speed for Zone A characters, which are
    // Animator-root-motion-driven (Base.cs sets animator.speed = velocity.magnitude, then the
    // BAKED clip's own root motion determines real displacement -- a nonlinear relationship,
    // not 1:1). Empirically calibrated, not guessed: an initial 1.3 (the 1.2-1.4 human-walk
    // midpoint) measured 1.82 m/s actual on a real trial -- overshooting the target band by
    // ~1.4x. 0.95 was back-derived from that measured ratio and re-verified: mean 1.286 m/s,
    // median 1.280 m/s actual (frames.csv position-delta measurement) -- squarely in the
    // 1.2-1.4 target band. See REPORT.md Session 29 for both calibration trials' numbers.
    // MAX_VEL below is kept equal to this (this file's own
    // pre-existing convention), since raising only one leaves the other as the effective,
    // unchanged cap.
    public const float DESIRED_SPEED = 0.95f;
    public const float T = 0.5f;
    public const float A = 2000f / 4;
    public const float B = 0.08f * 2;
    public const float K = 1.2E5f;
    public const float KAPPA = 2.4E5f;

    public const float WALL_A = 2000f * 4;
    public const float WALL_B = 0.08f * 3;
    public const float WALL_K = 1.2E5f;
    public const float WALL_KAPPA = 2.4E5f;

    public const float TAN_A = 2000f;
    public const float TAN_B = 0.08f;

    public const float MAX_VEL = 0.95f;//0.5f / 0.02f; -- raised with DESIRED_SPEED, Session 29 STEP 3
    public const float NEXT_NAV_MIN_DIST = 1.0f;
    public const float CLOSE_ENOUGH_MIN_DIST = 1.0f;
    public const float BACKWARD_DAMPENING = 20;
    public const float LATERAL_DAMPENING = 5;
    public const float ROBOT_REPULSION_DAMPENING_MIN = 0.5f;
    public const float ROBOT_REPULSION_DAMPENING_MAX = 1.0f;
}