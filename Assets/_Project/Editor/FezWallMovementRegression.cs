using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Run in the real Unity physics engine; temporary objects are destroyed after each case.
[InitializeOnLoad]
public static class FezWallMovementRegression
{
    private const string PendingKey = "Lichronicle.WallRegressionPending";
    private static int _warmup;
    private static readonly List<string> Results = new List<string>();
    private static GameObject _root;
    private static Vector3 _origin, _axis, _right;
    private static bool _useAuthoredCapsule;
    private static float _frameTime = 1f / 60f;

    static FezWallMovementRegression()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            {
                _warmup = 0;
                EditorApplication.update -= WaitForWarmup;
                EditorApplication.update += WaitForWarmup;
            }
        };
    }

    [MenuItem("Lichronicle/Tests/Wall movement regression %#F8")]
    public static void Run()
    {
        if (EditorApplication.isCompiling) return;
        SessionState.SetBool(PendingKey, true);
        if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
        else { _warmup = 0; EditorApplication.update += WaitForWarmup; }
    }

    private static void WaitForWarmup()
    {
        if (++_warmup < 12 || EditorApplication.isCompiling) return;
        EditorApplication.update -= WaitForWarmup;
        SessionState.SetBool(PendingKey, false);
        RunCases();
    }

    private static void RunCases()
    {
        Results.Clear();
        var boundarySnapshot = new List<string>();
        foreach (var live in UnityEngine.Object.FindObjectsOfType<PlayerController>())
        {
            var capsule = live.GetComponent<CharacterController>();
            boundarySnapshot.Add($"PLAYER {live.name} pos={live.transform.position:R} bounds={capsule.bounds} grounded={live.IsGrounded} support={live.StandingPlatform} slope={capsule.slopeLimit} scale={live.transform.lossyScale} speed={live.moveSpeed} jump={live.jumpHeight} gravity={live.gravity}");
            if (live.StandingPlatform != null)
                foreach (var collider in live.StandingPlatform.GetAllPlatformColliders())
                    if (collider != null) boundarySnapshot.Add($" SUPPORT {collider.name} {collider.bounds}");
        }
        foreach (var mesh in UnityEngine.Object.FindObjectsOfType<MeshCollider>())
            if (mesh.name.StartsWith("Plane")) boundarySnapshot.Add($"PLANE {mesh.name} pos={mesh.transform.position:R} rotation={mesh.transform.eulerAngles:R} bounds={mesh.bounds}");
        Directory.CreateDirectory("Logs");
        File.WriteAllLines("Logs/FezBoundarySnapshot.txt", boundarySnapshot);
        var originalView = FezWorldViewState.Active;
        for (int q = 0; q < 4; q++)
        {
            int quadrant = q;
            foreach (int sign in new[] { -1, 1 })
            {
                int direction = sign;
                Case($"jump between real depths view={q} direction={sign}", () => TestCrossDepthJump(quadrant, direction));
                Case($"walk off raised block into wall view={q} direction={sign}", () => TestRaisedBlock(quadrant, direction));
                Case($"return to routed block center view={q} direction={sign}", () => TestReturnToBlock(quadrant, direction, false));
                Case($"blocked return to routed block view={q} direction={sign}", () => TestReturnToBlock(quadrant, direction, true));
                Case($"route view={q} direction={sign}", () => TestRoute(quadrant, direction, 0));
                Case($"destination blocked view={q} direction={sign}", () => TestRoute(quadrant, direction, 1));
                Case($"alternate route view={q} direction={sign}", () => TestRoute(quadrant, direction, 2));
            }
            Case($"rotation occlusion keeps depth view={q}", () => TestHidden(quadrant));
            Case($"rotation occlusion behind tiled wall keeps depth view={q}", () => TestTiledWall(quadrant));
            Case($"routing resumes after leaving rotation occlusion view={q}", () => TestExitRotationOcclusion(quadrant));
            Case($"hidden projected landing cannot pull foreground view={q}", () => TestHiddenLanding(quadrant));
            foreach (int fps in new[] { 30, 60, 144 })
            {
                int rate = fps;
                Case($"authored capsule vertical wall entry view={q} fps={fps}", () => TestVerticalEntry(quadrant, rate));
                Case($"authored capsule repeated rotate jump fall view={q} fps={fps}", () => TestRepeatedJump(quadrant, rate));
                Case($"solid block side route view={q} fps={fps}", () => TestBlockSide(quadrant, rate, false));
                Case($"solid block blocked route view={q} fps={fps}", () => TestBlockSide(quadrant, rate, true));
                Case($"solid block drop around front view={q} fps={fps}", () => TestBlockDrop(quadrant, rate, false));
                Case($"solid block blocked drop view={q} fps={fps}", () => TestBlockDrop(quadrant, rate, true));
                Case($"tiled floor wall approach view={q} fps={fps}", () => TestTiledFloorApproach(quadrant, rate));
                Case($"jump against plane boundary view={q} fps={fps}", () => TestPlaneBoundaryJump(quadrant, rate, false));
                Case($"scaled player jump against plane view={q} fps={fps}", () => TestPlaneBoundaryJump(quadrant, rate, true));
            }
            Case($"solid block underside view={q}", () => TestBlockUnderside(quadrant));
            Case($"authored block native solidity view={q}", () => TestAuthoredBlockSolid(quadrant));
            Case($"drop past adjoining top tiles view={q}", () => TestTiledBlockDrop(quadrant));
            Case($"new landing aligns to platform center view={q}", () => TestProjectedLanding(quadrant));
            Case($"jump keeps depth view={q}", () => TestJump(quadrant));
            Case($"wall solid view={q}", () => TestSolid(quadrant));
        }
        for (int q = 0; q < 4; q++)
        {
            int quadrant = q;
            Case($"actual scene geometry replay view={q}", () => TestActualScene(quadrant));
        }
        if (originalView != null) { originalView.enabled = false; originalView.enabled = true; }
        Directory.CreateDirectory("Logs");
        File.WriteAllLines("Logs/FezWallMovementRegression.txt", Results);
        Debug.Log("Wall movement regression: " + string.Join("\n", Results));
    }

    private static void Case(string name, Action test)
    {
        try { test(); Results.Add("PASS " + name); }
        catch (Exception e) { Results.Add("FAIL " + name + ": " + e.Message); }
        finally
        {
            _useAuthoredCapsule = false;
            _frameTime = 1f / 60f;
            if (_root != null)
            {
                foreach (var controller in _root.GetComponentsInChildren<CharacterController>())
                    FezOneWayPlatform.ReleasePlayer(controller);
                UnityEngine.Object.DestroyImmediate(_root);
            }
            Physics.SyncTransforms();
        }
    }

    private static void Setup(int quadrant)
    {
        _origin = new Vector3(10000f, 1000f, 10000f);
        _root = new GameObject("TemporaryWallRegression");
        var view = new GameObject("View");
        view.transform.SetParent(_root.transform);
        view.transform.rotation = Quaternion.Euler(0, quadrant * 90, 0);
        var state = view.AddComponent<FezWorldViewState>();
        state.RefreshFromPivot();
        _axis = state.DepthAxisWorld;
        _right = Vector3.Cross(Vector3.up, _axis);
        Box("Ground", 0, -0.5f, 0, new Vector3(30, 1, 30));
    }

    private static GameObject Box(string name, float side, float y, float depth, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform);
        go.transform.position = _origin + _right * side + Vector3.up * y + _axis * depth;
        go.transform.rotation = Quaternion.LookRotation(_axis);
        go.AddComponent<BoxCollider>().size = size;
        return go;
    }

    private static PlayerController Player(float side, float depth, float y = 0.1f)
    {
        var go = new GameObject("TestPlayer");
        go.transform.SetParent(_root.transform);
        go.transform.position = _origin + _right * side + _axis * depth + Vector3.up * y;
        var cc = go.AddComponent<CharacterController>();
        cc.radius = 0.25f; cc.height = 1.6f; cc.center = Vector3.up * 0.8f; cc.skinWidth = 0.02f;
        if (_useAuthoredCapsule)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player/MorldPlayer.prefab").GetComponent<CharacterController>();
            cc.radius = source.radius; cc.height = source.height; cc.center = source.center;
            cc.skinWidth = source.skinWidth; cc.slopeLimit = source.slopeLimit;
            go.layer = source.gameObject.layer;
            // All fixtures specify foot height; the authored capsule has an offset root.
            go.transform.position += Vector3.up * (cc.height * 0.5f - cc.center.y);
        }
        var player = go.AddComponent<PlayerController>();
        player.enabled = false; // Drive the production simulation synchronously, without keyboard input.
        Physics.SyncTransforms();
        return player;
    }

    private static void Steps(PlayerController player, float horizontal, int count, bool jump = false)
    {
        for (int i = 0; i < count; i++)
        {
            player.SimulateFrame(_frameTime, horizontal, 0, jump && i == 0);
            Physics.SyncTransforms();
            Assert(!OverlapsWall(player), "player penetrated wall");
        }
    }

    private static bool OverlapsWall(PlayerController player)
    {
        var cc = player.GetComponent<CharacterController>();
        foreach (var wall in _root.GetComponentsInChildren<FezBackgroundWall>())
        foreach (var col in wall.GetBodyColliders())
            if (Physics.ComputePenetration(cc, cc.transform.position, cc.transform.rotation,
                    col, col.transform.position, col.transform.rotation, out _, out float distance) && distance > 0.025f)
                return true;
        return false;
    }

    private static void AddWall()
    {
        var wall = Box("Wall", 0, 1.5f, 0, new Vector3(1, 3, 1));
        int layer = LayerMask.NameToLayer("BackGroundWall");
        if (layer >= 0) wall.layer = layer;
        wall.AddComponent<FezBackgroundWall>();
    }

    private static PlayerController PlayerBeforeRotation(int quadrant, float y = 0.1f)
    {
        var view = FezWorldViewState.Active;
        view.transform.rotation = Quaternion.Euler(0, (quadrant + 1) * 90, 0);
        view.RefreshFromPivot();
        var player = Player(0, 1f, y);
        Steps(player, 0, 1); // Capture the previous committed view in the production motor.
        view.transform.rotation = Quaternion.Euler(0, quadrant * 90, 0);
        view.RefreshFromPivot();
        Physics.SyncTransforms();
        return player;
    }

    private static void TestHidden(int quadrant)
    {
        Setup(quadrant);
        AddWall();
        var player = PlayerBeforeRotation(quadrant);
        Steps(player, 0, 10); // Rotation itself must not change the world position.
        Steps(player, 1, 3);  // A little movement while still hidden must not release the hold.
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        Assert(Mathf.Abs(depth - 1f) < 0.02f, $"rotation occlusion pulled player foreground: {depth}");
    }

    private static void TestTiledWall(int quadrant)
    {
        Setup(quadrant);
        for (int i = -2; i <= 2; i++)
        {
            var wall = Box("Tile", i, 1.5f, 0, new Vector3(1, 3, 1));
            wall.AddComponent<FezBackgroundWall>();
        }
        var player = PlayerBeforeRotation(quadrant);
        Steps(player, 1, 8);
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        Assert(Mathf.Abs(depth - 1f) < 0.02f, "connected wall pulled hidden player foreground");
    }

    private static void TestExitRotationOcclusion(int quadrant)
    {
        Setup(quadrant);
        AddWall();
        var player = PlayerBeforeRotation(quadrant);
        for (int i = 0; i < 22; i++)
        {
            Steps(player, 1, 1);
            Assert(Mathf.Abs(Vector3.Dot(player.transform.position - _origin, _axis) - 1f) < 0.02f,
                "depth changed while walking out of occlusion");
        }
        Assert(Vector3.Dot(player.transform.position - _origin, _right) > 1f, "did not exit silhouette");
        Steps(player, 0, 2);
        Steps(player, -1, 35);
        Assert(Vector3.Dot(player.transform.position - _origin, _right) < -1f, "could not return past wall");
        Assert(Vector3.Dot(player.transform.position - _origin, _axis) < -0.75f,
            "routing remained locked after becoming visible");
    }

    private static void TestHiddenLanding(int quadrant)
    {
        Setup(quadrant);
        Box("TallWall", 0, 3, 0, new Vector3(3, 6, 1)).AddComponent<FezBackgroundWall>();
        var group = new GameObject("PlatformGroup");
        group.transform.SetParent(_root.transform);
        var floor = Box("ForegroundPlatform", 0, 0.5f, -1.5f, new Vector3(3, 1, 1));
        floor.transform.SetParent(group.transform, true);
        floor.AddComponent<FezOneWayPlatform>();
        var player = PlayerBeforeRotation(quadrant, 4f);
        for (int i = 0; i < 50; i++)
        {
            Steps(player, 0, 1);
            Assert(Mathf.Abs(Vector3.Dot(player.transform.position - _origin, _axis) - 1f) < 0.02f,
                "projected landing pulled a player hidden by rotation");
        }
    }

    private static void TestProjectedLanding(int quadrant)
    {
        Setup(quadrant);
        var group = new GameObject("PlatformGroup");
        group.transform.SetParent(_root.transform);
        var floor = Box("OffsetPlatform", 0, 0.5f, 0, new Vector3(8, 1, 2));
        floor.transform.SetParent(group.transform, true);
        floor.AddComponent<FezOneWayPlatform>();
        var player = Player(0, 2f, 4f);
        bool landed = false;
        for (int i = 0; i < 90; i++)
        {
            Steps(player, 0, 1);
            float y = player.transform.position.y - _origin.y;
            float depth = Vector3.Dot(player.transform.position - _origin, _axis);
            if (!player.IsGrounded) Assert(Mathf.Abs(depth - 2f) < 0.02f, "depth shifted before landing");
            if (player.IsGrounded && y > 0.9f)
            {
                Assert(Mathf.Abs(depth) < 0.025f, $"landing did not reach platform center: {depth}");
                Assert(HasRealPlatformSupport(player), "reported landing without real support");
                landed = true;
                break;
            }
        }
        Assert(landed, "missed projected platform");
    }

    private static void TestVerticalEntry(int quadrant, int fps)
    {
        Setup(quadrant);
        _useAuthoredCapsule = true; _frameTime = 1f / fps;
        Box("LowWall", 0, 1, 0, new Vector3(3, 2, 1)).AddComponent<FezBackgroundWall>();
        var player = Player(0, 1f, 3f);
        bool reentered = false;
        for (int i = 0; i < fps * 2; i++)
        {
            Steps(player, 0, 1);
            float feetY = player.GetComponent<CharacterController>().bounds.min.y - _origin.y;
            if (feetY < 1.9f)
            {
                reentered = true;
                Assert(Vector3.Dot(player.transform.position - _origin, player.DepthAxis) < -0.9f,
                    "falling from visible space reentered wall occlusion without routing");
            }
        }
        Assert(reentered && player.IsGrounded, "vertical traversal did not finish");
    }

    private static void TestRepeatedJump(int quadrant, int fps)
    {
        Setup(quadrant);
        _useAuthoredCapsule = true; _frameTime = 1f / fps;
        // Build a tiled wall, rather than a single collider, like the recorded level.
        for (int x = -1; x <= 1; x++)
        for (int y = 0; y < 2; y++)
            Box("WallTile", x, y + 0.5f, 0, Vector3.one).AddComponent<FezBackgroundWall>();
        var player = PlayerBeforeRotation(quadrant);
        Steps(player, 0, fps / 2);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            if (cycle > 0)
            {
                var view = FezWorldViewState.Active;
                view.transform.rotation = Quaternion.Euler(0, (quadrant + cycle * 2) * 90, 0);
                view.RefreshFromPivot();
                Steps(player, 0, 3);
            }
            Assert(player.IsGrounded, "not grounded before repeated jump");
            float initialDepth = Vector3.Dot(player.transform.position - _origin, player.DepthAxis);
            Assert(initialDepth > 0.8f, "rotation fixture should start behind wall");
            bool cleared = false, routed = false;
            Steps(player, 0, 1, true);
            for (int i = 0; i < fps * 2; i++)
            {
                Steps(player, 0, 1);
                float feetY = player.GetComponent<CharacterController>().bounds.min.y - _origin.y;
                float depth = Vector3.Dot(player.transform.position - _origin, player.DepthAxis);
                if (feetY > 2.1f) cleared = true;
                if (!cleared) Assert(Mathf.Abs(depth - initialDepth) < 0.03f, "pulled forward before clearing occlusion");
                if (cleared && feetY < 1.9f)
                {
                    routed = true;
                    Assert(depth < -0.9f, $"cycle {cycle}: fell back behind visible wall, depth={depth}");
                }
            }
            Assert(cleared && routed && player.IsGrounded, "repeated cycle did not complete");
        }
    }

    private static void TestActualScene(int quadrant)
    {
        var platforms = UnityEngine.Object.FindObjectsOfType<FezOneWayPlatform>();
        var walls = UnityEngine.Object.FindObjectsOfType<FezBackgroundWall>();
        Assert(platforms.Length > 0 && walls.Length > 0, "open a level containing real platforms and walls");
        Collider startingBody = null;
        foreach (var platform in platforms)
        foreach (var body in platform.GetAllPlatformColliders())
        {
            if (body == null || !body.enabled || body.isTrigger ||
                body.GetComponentInParent<FezDepthProxyFollower>() != null) continue;
            if (startingBody == null || body.bounds.max.y > startingBody.bounds.max.y) startingBody = body;
        }
        Assert(startingBody != null, "no authored platform body available");
        _origin = Vector3.zero;
        _root = new GameObject("TemporaryActualSceneReplay");
        var viewObject = new GameObject("ReplayView");
        viewObject.transform.SetParent(_root.transform);
        viewObject.transform.rotation = Quaternion.Euler(0, quadrant * 90, 0);
        var view = viewObject.AddComponent<FezWorldViewState>();
        view.RefreshFromPivot();
        _axis = view.DepthAxisWorld; _right = Vector3.Cross(Vector3.up, _axis);
        _useAuthoredCapsule = true;
        Vector3 foot = startingBody.bounds.center;
        foot.y = startingBody.bounds.max.y + 0.1f;
        var player = Player(Vector3.Dot(foot, _right), Vector3.Dot(foot, _axis), foot.y);
        var cc = player.GetComponent<CharacterController>();
        var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player/MorldPlayer.prefab").GetComponent<PlayerController>();
        player.moveSpeed = source.moveSpeed; player.jumpHeight = source.jumpHeight;
        player.gravity = source.gravity; player.acceleration = source.acceleration; player.deceleration = source.deceleration;
        var trace = new List<string> { "frame,yaw,input,x,y,z,grounded" };
        try
        {
            Steps(player, 0, 20);
            int completed = 0;
            for (int i = 0; i < 600; i++)
            {
                if (i % 120 == 0)
                {
                    viewObject.transform.rotation = Quaternion.Euler(0, (quadrant + i / 120) * 90, 0);
                    view.RefreshFromPivot();
                }
                float input = i % 90 < 35 ? 1f : i % 90 < 70 ? -1f : 0f;
                Vector3 before = player.transform.position;
                Vector3 depthAxis = player.DepthAxis;
                player.SimulateFrame(_frameTime, input, 0, i % 90 == 0 && player.IsGrounded);
                Physics.SyncTransforms();
                Vector3 position = player.transform.position;
                trace.Add($"{i},{viewObject.transform.eulerAngles.y},{input},{position.x:R},{position.y:R},{position.z:R},{player.IsGrounded}");
                foreach (var wall in walls)
                foreach (var body in wall.GetBodyColliders())
                    if (body != null && body.enabled && !body.isTrigger &&
                        Physics.ComputePenetration(cc, position, cc.transform.rotation, body, body.transform.position,
                            body.transform.rotation, out _, out float distance))
                        Assert(distance <= cc.skinWidth + 0.025f, $"frame {i}: penetrated authored wall {wall.name} by {distance}");
                float lateralStep = Mathf.Abs(Vector3.Dot(position - before, Vector3.Cross(Vector3.up, depthAxis)));
                Assert(lateralStep <= player.moveSpeed * _frameTime + 0.06f, $"frame {i}: route caused sideways pop {lateralStep}");
                if (Vector3.Dot(position - before, depthAxis) > 0.04f)
                    Assert(player.IsGrounded && HasRealPlatformSupport(player),
                        $"frame {i}: depth changed without a real landing");
                completed++;
                if (cc.bounds.min.y < foot.y - 20f) break; // Walking off the authored level is permitted.
            }
            Assert(completed >= 30, "scene replay ended too early");
        }
        finally
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllLines($"Logs/FezActualSceneReplay-{quadrant}.csv", trace);
        }
    }

    private static bool HasRealPlatformSupport(PlayerController player)
    {
        var platform = player.StandingPlatform;
        if (platform == null) return false;
        var cc = player.GetComponent<CharacterController>();
        Vector3 feet = cc.bounds.center;
        feet.y = cc.bounds.min.y;
        return platform.TryGetAnchoredFloor(feet, feet, 0.025f, out _, player.DepthAxis, cc.radius + 0.05f);
    }

    private static void TestCrossDepthJump(int quadrant, int direction)
    {
        Setup(quadrant);
        _useAuthoredCapsule = true;
        var sourceGroup = new GameObject("SourceGroup"); sourceGroup.transform.SetParent(_root.transform);
        var targetGroup = new GameObject("TargetGroup"); targetGroup.transform.SetParent(_root.transform);
        var source = Box("SourcePlatform", -2 * direction, 0.5f, 1f, new Vector3(2, 1, 1));
        source.transform.SetParent(sourceGroup.transform, true);
        source.AddComponent<FezOneWayPlatform>();
        var target = Box("TargetPlatform", 2 * direction, 0.5f, -1f, new Vector3(2, 1, 1));
        target.transform.SetParent(targetGroup.transform, true);
        var targetPlatform = target.AddComponent<FezOneWayPlatform>();
        var player = Player(-2 * direction, 1f, 1.1f);
        player.jumpHeight = 1.5f;
        Steps(player, 0, 30);
        Assert(player.IsGrounded && HasRealPlatformSupport(player), "did not start on physical source platform");
        bool leftSource = false, landed = false;
        for (int i = 0; i < 70; i++)
        {
            Steps(player, direction, 1, i == 0);
            if (!player.IsGrounded)
            {
                leftSource = true;
                float depth = Vector3.Dot(player.transform.position - _origin, _axis);
                Assert(Mathf.Abs(depth - 1f) < 0.03f, "jump aligned before contacting target");
            }
            if (leftSource && player.IsGrounded)
            {
                Assert(player.StandingPlatform == targetPlatform, "did not land on the destination platform");
                Assert(HasRealPlatformSupport(player), "destination contact is only projected in 2D");
                float depth = Vector3.Dot(player.transform.position - _origin, _axis);
                Assert(Mathf.Abs(depth + 1f) < 0.025f, $"3D depth missed destination center: {depth}");
                Steps(player, 0, 10);
                Assert(player.IsGrounded && HasRealPlatformSupport(player), "lost physical support after landing");
                landed = true;
                break;
            }
        }
        Assert(leftSource && landed, "cross-depth jump did not complete");
    }

    private static FezOneWayPlatform SolidBlock(string name, float side, float y, float depth, Vector3 size)
    {
        var group = new GameObject(name + "Group"); group.transform.SetParent(_root.transform);
        var block = Box(name, side, y, depth, size);
        block.transform.SetParent(group.transform, true);
        block.layer = LayerMask.NameToLayer("FezPlatform");
        return block.AddComponent<FezOneWayPlatform>();
    }

    private static void AssertNoBlockPenetration(PlayerController player, FezOneWayPlatform platform)
    {
        var cc = player.GetComponent<CharacterController>();
        foreach (var body in platform.GetSolidBodyColliders())
        {
            Assert(!Physics.GetIgnoreCollision(cc, body), "solid block collision was disabled");
            if (Physics.ComputePenetration(cc, cc.transform.position, cc.transform.rotation,
                body, body.transform.position, body.transform.rotation, out _, out float distance))
                Assert(distance <= cc.skinWidth + 0.025f, $"penetrated solid block by {distance}");
        }
    }

    private static void TestBlockSide(int quadrant, int fps, bool blocked)
    {
        Setup(quadrant); _useAuthoredCapsule = true; _frameTime = 1f / fps;
        var block = SolidBlock("SolidSide", 0, 1f, 0, new Vector3(1, 2, 1));
        if (blocked) Box("BlockedFront", 0, 1f, -1f, new Vector3(3, 2, 0.3f));
        var player = Player(-2, 1f);
        for (int i = 0; i < fps; i++)
        { Steps(player, 1, 1); AssertNoBlockPenetration(player, block); }
        float side = Vector3.Dot(player.transform.position - _origin, _right);
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        if (blocked) Assert(side < -0.5f && Mathf.Abs(depth - 1f) < 0.03f, "entered blocked block route");
        else Assert(side > 1f && depth < -0.9f, $"did not route around solid block: side={side}, depth={depth}");
    }

    private static void TestBlockDrop(int quadrant, int fps, bool blocked)
    {
        Setup(quadrant); _useAuthoredCapsule = true; _frameTime = 1f / fps;
        var upper = SolidBlock("ThickUpper", 0, 3f, 1f, new Vector3(4, 2, 2));
        var lower = SolidBlock("Lower", 0, 0.5f, -0.75f, new Vector3(4, 1, 2));
        var player = Player(0, 1f, 4.1f);
        var other = Player(1f, 1f, 4.1f);
        Steps(player, 0, fps / 2); Steps(other, 0, fps / 2);
        Assert(player.StandingPlatform == upper && player.IsGrounded, "not standing on upper block");
        if (blocked) Box("DropPathBlocker", 0, 4.7f, 0.1f, new Vector3(4, 1, 0.2f));
        Physics.SyncTransforms();
        Vector3 before = player.transform.position;
        player.SimulateFrame(_frameTime, 0, -1f, true);
        Physics.SyncTransforms();
        for (int i = 0; i < fps * 2; i++)
        {
            Steps(player, 0, 1); Steps(other, 0, 1);
            AssertNoBlockPenetration(player, upper); AssertNoBlockPenetration(player, lower);
            Assert(other.IsGrounded && other.StandingPlatform == upper, "drop changed another player's support");
        }
        if (blocked)
            Assert(player.IsGrounded && player.StandingPlatform == upper &&
                (player.transform.position - before).sqrMagnitude < 0.0025f, "blocked drop moved player");
        else
            Assert(player.IsGrounded && player.StandingPlatform == lower && HasRealPlatformSupport(player),
                $"drop missed lower block: y={player.GetComponent<CharacterController>().bounds.min.y - _origin.y}");
    }

    private static void TestPlaneBoundaryJump(int quadrant, int fps, bool scaled)
    {
        Setup(quadrant); _useAuthoredCapsule = true; _frameTime = 1f / fps;
        var wall = GameObject.CreatePrimitive(PrimitiveType.Plane);
        wall.name = "SolidRoomBoundaryPlane";
        wall.transform.SetParent(_root.transform);
        wall.transform.position = _origin + Vector3.up * 5f;
        wall.transform.rotation = Quaternion.FromToRotation(Vector3.up, -_right);
        wall.transform.localScale = new Vector3(2, 1, 2);
        var player = Player(-1f, 0);
        var cc = player.GetComponent<CharacterController>();
        if (scaled)
        {
            player.transform.localScale = Vector3.one * 0.5f;
            Physics.SyncTransforms();
            player.transform.position += Vector3.up * (_origin.y + 0.1f - cc.bounds.min.y);
            player.moveSpeed = 5f; player.jumpHeight = 1.3f;
            Physics.SyncTransforms();
        }
        Steps(player, 1, fps / 2);
        float maximum = cc.bounds.min.y;
        for (int i = 0; i < fps * 3; i++)
        {
            Steps(player, 1, 1, i == 0);
            maximum = Mathf.Max(maximum, cc.bounds.min.y);
            Assert(Vector3.Dot(cc.bounds.center - _origin, _right) < 0.03f, "crossed boundary plane");
        }
        Assert(maximum > _origin.y + 1f, "boundary jump did not rise");
        Assert(cc.bounds.min.y < _origin.y + 0.1f && player.IsGrounded,
            $"stuck floating on plane: feet={cc.bounds.min.y - _origin.y}, grounded={player.IsGrounded}");
    }

    private static void TestTiledFloorApproach(int quadrant, int fps)
    {
        Setup(quadrant); _useAuthoredCapsule = true; _frameTime = 1f / fps;
        for (int side = -4; side <= 4; side++)
        for (int depth = -3; depth <= 2; depth++)
            SolidBlock("FloorTile", side, -0.5f, depth, Vector3.one);
        AddWall();
        var player = Player(-2, 1f);
        Steps(player, 0, fps / 2);
        // Switching views while standing on real floor tiles must not latch the occlusion hold.
        var view = FezWorldViewState.Active;
        view.transform.rotation = Quaternion.Euler(0, (quadrant + 1) * 90, 0);
        view.RefreshFromPivot();
        Steps(player, 0, 1);
        view.transform.rotation = Quaternion.Euler(0, quadrant * 90, 0);
        view.RefreshFromPivot();
        Steps(player, 0, 1);
        bool holding = (bool)typeof(PlayerController).GetField("_holdDepthAfterOccludedRotation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(player);
        Assert(!holding, $"floor incorrectly holds depth after rotation: feet={player.GetComponent<CharacterController>().bounds.min.y - _origin.y}");
        Steps(player, 1, fps);
        float position = Vector3.Dot(player.transform.position - _origin, _right);
        Assert(position > 1f, $"stuck beside wall on tiled floor: side={position}, depth={Vector3.Dot(player.transform.position - _origin, _axis)}, feet={player.GetComponent<CharacterController>().bounds.min.y - _origin.y}");
    }

    private static void TestTiledBlockDrop(int quadrant)
    {
        Setup(quadrant); _useAuthoredCapsule = true;
        var rear = SolidBlock("RearTile", 0, 2.5f, 1f, new Vector3(3, 1, 1));
        var front = SolidBlock("FrontTile", 0, 2.5f, 0f, new Vector3(3, 1, 1));
        var player = Player(0, 1f, 3.1f);
        Steps(player, 0, 30);
        Assert(player.StandingPlatform == rear, "not on rear tile");
        player.SimulateFrame(_frameTime, 0, -1f, true);
        Physics.SyncTransforms();
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        Assert(depth < -0.9f, $"drop stopped on adjoining front tile: {depth}");
        for (int i = 0; i < 90; i++)
        {
            Steps(player, 0, 1);
            AssertNoBlockPenetration(player, rear); AssertNoBlockPenetration(player, front);
        }
        Assert(player.GetComponent<CharacterController>().bounds.min.y < _origin.y + 0.1f,
            "did not fall past tiled platform");
    }

    private static void TestBlockUnderside(int quadrant)
    {
        Setup(quadrant); _useAuthoredCapsule = true;
        var block = SolidBlock("CeilingBlock", 0, 3f, 0, new Vector3(4, 1, 2));
        var player = Player(0, 0);
        Steps(player, 0, 30);
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < 120; i++)
        {
            Steps(player, 0, 1, i == 0);
            maximum = Mathf.Max(maximum, player.GetComponent<CharacterController>().bounds.max.y - _origin.y);
            AssertNoBlockPenetration(player, block);
        }
        Assert(maximum > 1.3f && maximum < 2.55f, $"underside did not stop jump: head={maximum}");
    }

    private static void TestAuthoredBlockSolid(int quadrant)
    {
        Setup(quadrant); _useAuthoredCapsule = true;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Building blocks/TestBlocks/Block1x1.prefab");
        var block = UnityEngine.Object.Instantiate(prefab, _root.transform);
        block.transform.position = _origin + Vector3.up * 0.5f;
        block.transform.rotation = Quaternion.LookRotation(_axis);
        var platform = block.GetComponentInChildren<FezOneWayPlatform>();
        var player = Player(-2, 0);
        Steps(player, 0, 1);
        player.GetComponent<CharacterController>().Move(_right * 4);
        Physics.SyncTransforms();
        AssertNoBlockPenetration(player, platform);
        Assert(Vector3.Dot(player.transform.position - _origin, _right) < -0.4f,
            "native controller passed through authored block");
    }

    private static void TestReturnToBlock(int quadrant, int direction, bool blocked)
    {
        Setup(quadrant);
        AddWall();
        var group = new GameObject("ReturnPlatformGroup");
        group.transform.SetParent(_root.transform);
        var block = Box("ReturnBlock", -direction, 0.5f, 1f, Vector3.one);
        block.transform.SetParent(group.transform, true);
        var platform = block.AddComponent<FezOneWayPlatform>();
        var player = Player(-direction, 1f, 1.1f);
        Steps(player, 0, 30);
        bool routed = false;
        for (int i = 0; i < 30; i++)
        {
            Steps(player, direction, 1);
            if (Vector3.Dot(player.transform.position - _origin, _axis) < -0.75f)
            { routed = true; break; }
        }
        Assert(routed && player.StandingPlatform == platform, "did not route while retaining source block");
        GameObject obstacle = blocked
            ? Box("ReturnBlocker", -direction, 1.6f, 1f, new Vector3(0.4f, 1f, 0.4f)) : null;
        Physics.SyncTransforms();
        for (int i = 0; i < 30; i++)
        {
            float side = Vector3.Dot(player.transform.position - _origin, _right) * direction;
            if (side <= -0.99f) break;
            Steps(player, -direction, 1);
        }
        Steps(player, 0, 20);
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        Assert(player.IsGrounded && player.StandingPlatform == platform, "lost source foothold while returning");
        if (blocked)
        {
            Assert(depth < -0.75f, $"returned through blocker: {depth}");
            obstacle.SetActive(false);
            Physics.SyncTransforms();
            Steps(player, 0, 20);
            depth = Vector3.Dot(player.transform.position - _origin, _axis);
        }
        Assert(Mathf.Abs(depth - 1f) < 0.025f, $"did not restore source block center: {depth}");
        Steps(player, 0, 30);
        Assert(Mathf.Abs(Vector3.Dot(player.transform.position - _origin, _axis) - 1f) < 0.025f,
            "restored depth oscillated");
    }

    private static void TestRaisedBlock(int quadrant, int direction)
    {
        Setup(quadrant);
        AddWall();
        var group = new GameObject("PlatformGroup");
        group.transform.SetParent(_root.transform);
        var block = Box("StartingBlock", -direction, 0.5f, 1f, Vector3.one);
        block.transform.SetParent(group.transform, true);
        block.AddComponent<FezOneWayPlatform>();
        var player = Player(-direction, 1f, 1.1f);
        Steps(player, 0, 30);
        Assert(player.IsGrounded && player.StandingPlatform != null, "did not start on raised block");
        bool routed = false;
        for (int i = 0; i < 35; i++)
        {
            Steps(player, direction, 1);
            float depth = Vector3.Dot(player.transform.position - _origin, _axis);
            if (depth < -0.75f) routed = true;
            if (routed) Assert(depth < -0.75f, $"support pulled player back behind wall: {depth}");
        }
        float side = Vector3.Dot(player.transform.position - _origin, _right) * direction;
        Assert(routed && side > 1f, $"stuck at platform/wall edge: side={side}, depth={Vector3.Dot(player.transform.position - _origin, _axis)}");
    }

    private static void TestRoute(int quadrant, int direction, int blocker)
    {
        Setup(quadrant);
        AddWall();
        if (blocker == 1) Box("DestinationBlocker", 0, 1, -0.9f, new Vector3(3, 2, 0.3f));
        if (blocker == 2) Box("PathBlocker", -direction * 0.9f, 1, 0.1f, new Vector3(0.45f, 2, 0.2f));
        var player = Player(-direction * 2f, 1f);
        Steps(player, direction, 40);
        float side = Vector3.Dot(player.transform.position - _origin, _right) * direction;
        float depth = Vector3.Dot(player.transform.position - _origin, _axis);
        if (blocker != 1)
        {
            Assert(side > 1f, $"did not pass wall: side={side}, depth={depth}, y={player.transform.position.y - _origin.y}, axis={player.DepthAxis}, walls={_root.GetComponentsInChildren<FezBackgroundWall>().Length}, bounds={player.GetComponent<CharacterController>().bounds}");
            Assert(depth < -0.75f, $"not in front of wall: {depth}, y={player.transform.position.y - _origin.y}, axis={player.DepthAxis}, bounds={player.GetComponent<CharacterController>().bounds}");
        }
        else
        {
            Assert(side < -0.5f, $"entered blocked silhouette: {side}");
            Assert(Mathf.Abs(depth - 1f) < 0.03f, $"blocked route changed depth: {depth}");
        }
    }

    private static void TestJump(int quadrant)
    {
        Setup(quadrant);
        var floor = Box("WidePlatform", 0, 0.5f, 0, new Vector3(8, 1, 8));
        var group = new GameObject("PlatformGroup");
        group.transform.SetParent(_root.transform);
        floor.transform.SetParent(group.transform, true);
        floor.AddComponent<FezOneWayPlatform>();
        var player = Player(0, 2f, 1.1f);
        Steps(player, 0, 30);
        Assert(player.IsGrounded, $"not grounded before jump, y={player.transform.position.y - _origin.y}, bounds={player.GetComponent<CharacterController>().bounds}, platform={floor.GetComponent<BoxCollider>().bounds}");
        float startDepth = Vector3.Dot(player.transform.position - _origin, _axis);
        float maxY = player.transform.position.y;
        for (int i = 0; i < 110; i++)
        {
            Steps(player, 0, 1, i == 0);
            maxY = Mathf.Max(maxY, player.transform.position.y);
            float depth = Vector3.Dot(player.transform.position - _origin, _axis);
            Assert(Mathf.Abs(depth - startDepth) < 0.02f, $"jump changed depth: {depth}");
        }
        Assert(maxY > _origin.y + 3f, "jump never rose");
        Assert(player.IsGrounded, "jump did not land");
    }

    private static void TestSolid(int quadrant)
    {
        Setup(quadrant);
        AddWall();
        var player = Player(0, -2);
        var cc = player.GetComponent<CharacterController>();
        cc.Move(_axis * 4f);
        Physics.SyncTransforms();
        Assert(Vector3.Dot(player.transform.position - _origin, _axis) < -0.7f, "native controller passed through wall");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
