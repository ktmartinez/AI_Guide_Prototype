using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Per-scene mover specs used by MovingObjectsSceneSetup. Each themed scene gets movers that fit its setting, existing objects first:
//   Monster Pet Shop (medieval fantasy village) - the market's Delivery Cart becomes a horse-drawn wagon, a dragon circles the village
//   Flower Shop (medieval countryside farm) - the pasture horses, a cow and pigs walk and graze; sparrows over the fields
//   Alien Spaceship Repair Shop (sci-fi) - the Operational Space Truck flies to the customer launch pad and back, a floating rock
//      drifts, a UFO darts around the control tower and launch pad, and two alien creatures roam
//   Witch Cottage (fantasy bog) - the floating potions drift, an enchanted broom circles the cottage, a ghost haunts the lilypads
//   Kitchen and Pharmacy (modern city) - buses, cars and a van in right-hand lanes, pedestrians on sidewalks, a corgi, sparrows
// Coordinates are world space and come from Tools > Moving Objects > Survey Themed Scenes and the overview captures.
public static partial class MovingObjectsSceneSetup
{
    // Sounds in Assets/Prefabs/Moving Objects/Audio (missing ones are skipped with a warning until they're added)
    private const string HorseCartLoop = "horse-cart-loop.mp3";
    private const string HorseWalkLoop = "horse-walk-loop.mp3";
    private const string HorseWhinny = "horse-whinny.mp3";
    private const string CowMoo = "cow-moo.mp3";
    private const string DragonWingsLoop = "dragon-wings-loop.mp3";
    private const string DragonRoar = "dragon-roar.mp3";
    private const string HoverHumLoop = "hover-hum-loop.mp3";
    private const string ThrusterBurst = "thruster-burst.mp3";
    private const string UfoHumLoop = "ufo-hum-loop.mp3";
    private const string AlienChirp = "alien-chirp.mp3";
    private const string CreatureGrowl = "creature-growl.mp3";
    private const string MagicShimmerLoop = "magic-shimmer-loop.mp3";
    private const string BroomWhooshLoop = "broom-whoosh-loop.mp3";
    private const string GhostMoan = "ghost-moan.mp3";
    private const string FootstepsLoop = "Assets/Resources/Audio/general-walk.mp3";

    // Models already in the project
    private const string HorseModel = "Assets/Environment/General/Farm/Farm Animal Pack-glb/Horse.glb";
    private const string CowModel = "Assets/Environment/General/Farm/Farm Animal Pack-glb/Cow.glb";
    private const string MonsterPets = "Assets/Environment/Monster Pet Shop/MyMonsterPet/";
    private const string SparrowPrefab = MoverFolder + "Sparrow.prefab";

    private static readonly string[] Grazing = { "Eating", "Idle" };
    private static readonly string[] Standing = { "Idle" };
    private static readonly string[] Walking = { "Walk" };
    private static readonly string[] Ambling = { "WalkSlow", "Walk" };
    private static readonly string[] Hovering = { "Flying_Idle", "Fly_Idle", "Idle" };
    private static readonly string[] Flying = { "Fast_Flying", "Flying", "Fly" };

    private static List<SceneSpec> GetSceneSpecs()
    {
        return new List<SceneSpec>
        {
            MonsterPetShop(), FlowerShop(), AlienSpaceshipRepairShop(), WitchCottage(), Kitchen(), Pharmacy()
        };
    }

    // ---------- Medieval fantasy village ----------

    private static SceneSpec MonsterPetShop()
    {
        SceneSpec scene = new SceneSpec("Monster Pet Shop");

        // The market's delivery cart, now pulled by a horse along the rock path from the market to the pet shop and back
        MoverSpec wagon = new MoverSpec
        {
            Name = "Delivery Cart",
            Existing = "Market Deliveries/Delivery Cart",
            Route = new RouteSpec("Delivery Cart Route", loop: false)
                .Ground(24, 5, 36.8f).Wait(12)
                .Ground(14, 5, 36.6f).Ground(4, 5, 36.3f).Ground(-4, 5, 36.2f)
                .Ground(-8, 5, 31).Ground(-8, 5, 22).Ground(-8, 5, 13).Wait(10),
            Speed = 1.6f, TurnSharpness = 1.2f, Warn = true,
            ClipModel = HorseModel, AnimatorOn = "Cart Horse", IdleClips = Standing, MoveClips = Walking,
            Audio = new AudioSpec { Loop = HorseCartLoop, Occasional = new[] { HorseWhinny }, Interval = 35f, WaitingVolume = 0f, MaxDistance = 30f, Priority = 100 },
        };
        wagon.Setup = cart => HitchHorse(cart, wagon);
        scene.Add(wagon);

        // A dragon circling high over the village and the lake - something no visitor has met in real life
        scene.Add(new MoverSpec
        {
            Name = "Flying Dragon",
            Asset = MonsterPets + "Dragon Evolved.glb", Size = 7f,
            Route = new RouteSpec("Dragon Flight")
                .Air(-30, 30, 40).Air(10, 36, 58).Air(45, 28, 42).Air(58, 20, 2).Air(40, 31, -35)
                .Air(0, 40, -48).Air(-35, 25, -22).Air(-46, 33, 14),
            Speed = 9f, Bob = 1.5f, BobFrequency = 0.3f, Warn = false, YawOnly = false,
            ClipModel = MonsterPets + "Dragon Evolved.glb", IdleClips = Hovering, MoveClips = Flying,
            Audio = new AudioSpec { Loop = DragonWingsLoop, Occasional = new[] { DragonRoar }, Interval = 25f, MinDistance = 8f, MaxDistance = 70f, Priority = 90 },
        });

        // Two sparrows that flit between the market roofs and land in the square. (The pet shop's monsters stay on their tables:
        // they're shop stock, and the shop floor is too crowded for them to wander without walking through furniture.)
        RouteSpec marketFlight = new RouteSpec("Market Sparrows")
            .Air(18, 12, 40).Air(32, 14, 50).Air(44, 11, 41).Ground(34, 5, 38).Wait(6).Air(24, 9, 34);
        scene.Add(new MoverSpec { Name = "Sparrow", Asset = SparrowPrefab, Route = marketFlight });
        scene.Add(new MoverSpec { Name = "Sparrow", Asset = SparrowPrefab, Route = marketFlight, CycleOffset = 0.45f });
        return scene;
    }

    // Hitches a horse in front of the cart (once), and points the cart's long side along its travel direction
    private static void HitchHorse(GameObject cart, MoverSpec spec)
    {
        Quaternion rotation = cart.transform.rotation;
        cart.transform.rotation = Quaternion.identity;

        Transform horse = cart.transform.Find("Cart Horse");
        if (horse == null)
        {
            horse = ((GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HorseModel), cart.transform)).transform;
            horse.name = "Cart Horse";
        }
        horse.localScale = Vector3.one / Mathf.Max(0.0001f, cart.transform.lossyScale.x);
        horse.localPosition = Vector3.zero;
        horse.localRotation = Quaternion.identity;
        // A cart horse about 2.2m tall to the top of its head, whatever the model's own size
        horse.localScale *= 2.2f / Mathf.Max(0.01f, GetBounds(horse.gameObject).size.y);

        // Bounds without the horse tell us which way the cart is long
        horse.gameObject.SetActive(false);
        Bounds cartBounds = GetBounds(cart);
        horse.gameObject.SetActive(true);
        bool longAlongX = cartBounds.size.x >= cartBounds.size.z;
        Vector3 ahead = longAlongX ? Vector3.right : Vector3.forward;
        spec.RotationOffset = longAlongX ? new Vector3(0f, -90f, 0f) : Vector3.zero;

        horse.rotation = Quaternion.LookRotation(ahead, Vector3.up);
        Bounds horseBounds = GetBounds(horse.gameObject);
        float cartHalf = longAlongX ? cartBounds.extents.x : cartBounds.extents.z;
        float horseHalf = longAlongX ? horseBounds.extents.x : horseBounds.extents.z;
        Vector3 target = cartBounds.center + ahead * (cartHalf + horseHalf + 0.2f);
        target.y = cartBounds.min.y;
        horse.position += new Vector3(target.x - horseBounds.center.x, target.y - horseBounds.min.y, target.z - horseBounds.center.z);

        Renderer horseMesh = horse.GetComponentsInChildren<Renderer>(true).OrderByDescending(r => r.bounds.size.sqrMagnitude).FirstOrDefault();
        if (horseMesh != null && horseMesh.GetComponent<Collider>() == null) AddNamedCollider(horseMesh, "Cart Horse");

        cart.transform.rotation = rotation;
    }

    // ---------- Medieval countryside farm ----------

    private static SceneSpec FlowerShop()
    {
        SceneSpec scene = new SceneSpec("Flower Shop");
        AudioSpec horseSounds = new AudioSpec { Loop = HorseWalkLoop, Occasional = new[] { HorseWhinny }, Interval = 40f, WaitingVolume = 0f, MaxDistance = 20f };

        // The second horse was parented to the first, so it's moved out before either walks
        scene.Add(new MoverSpec
        {
            Name = "Horse", Existing = "Horse (1)", Detach = true,
            Route = new RouteSpec("Grazing Walk East", loop: false) { Relative = true }
                .Ground(0, 0, 0).Wait(10).Ground(4, 0, -2).Ground(8, 0, 1).Wait(14),
            Speed = 1.1f, Warn = true,
            ClipModel = HorseModel, IdleClips = Grazing, MoveClips = Walking,
            Audio = horseSounds,
        });
        scene.Add(new MoverSpec
        {
            Name = "Horse", Existing = "Fields and Pens/Horse",
            Route = new RouteSpec("Grazing Walk West") { Relative = true }
                .Ground(0, 0, 0).Wait(8).Ground(-3, 0, 4).Ground(-1, 0, 8).Wait(12).Ground(3, 0, 7).Ground(3, 0, 3),
            Speed = 1.2f, Warn = true,
            ClipModel = HorseModel, IdleClips = Grazing, MoveClips = Walking,
            Audio = horseSounds,
        });
        // Two cows amble around their pen (the pig model has no walk animation, so the pigs stay put)
        scene.Add(new MoverSpec
        {
            Name = "Cow", Existing = "Farm-91wMLb9kKo (2)/Cow",
            Route = new RouteSpec("Cow Graze", loop: false) { Relative = true }
                .Ground(0, 0, 0).Wait(12).Ground(2.5f, 0, 1.5f).Wait(10),
            Speed = 0.7f, Warn = true,
            ClipModel = CowModel, IdleClips = Grazing, MoveClips = Ambling,
            Audio = new AudioSpec { Occasional = new[] { CowMoo }, Interval = 30f, MaxDistance = 25f },
        });
        scene.Add(new MoverSpec
        {
            Name = "Cow", Existing = "Farm-91wMLb9kKo (2)/Cow (1)",
            Route = new RouteSpec("Cow (1) Graze", loop: false) { Relative = true }
                .Ground(0, 0, 0).Wait(9).Ground(-1.5f, 0, -2.5f).Wait(14),
            Speed = 0.7f, Warn = true,
            ClipModel = CowModel, IdleClips = Grazing, MoveClips = Ambling,
            Audio = new AudioSpec { Occasional = new[] { CowMoo }, Interval = 37f, MaxDistance = 25f },
        });
        RouteSpec fieldFlight = new RouteSpec("Field Sparrows")
            .Air(15, 12, 30).Air(35, 15, 54).Air(48, 10, 38).Ground(38, 5.5f, 28).Wait(6).Air(25, 9, 24);
        scene.Add(new MoverSpec { Name = "Sparrow", Asset = SparrowPrefab, Route = fieldFlight });
        scene.Add(new MoverSpec { Name = "Sparrow", Asset = SparrowPrefab, Route = fieldFlight, CycleOffset = 0.5f });
        return scene;
    }

    // ---------- Sci-fi ----------

    private static SceneSpec AlienSpaceshipRepairShop()
    {
        SceneSpec scene = new SceneSpec("Alien Spaceship Repair Shop");

        // The operational truck lifts off from its spot, flies to the customer launch pad, lands, then loops back around
        scene.Add(new MoverSpec
        {
            Name = "Operational Space Truck", Existing = "Wasteland Civilization Pieces/Operational Space Truck",
            Route = new RouteSpec("Space Truck Flight")
                .Ground(-21, -4, -16.4f).Wait(18).Air(-21, 4, -16.4f).Air(-6, 10, -30).Air(20, 9, -26).Air(33, 5, -13.4f)
                .Ground(33, -3, -13.4f).Wait(14).Air(33, 8, -13.4f).Air(46, 20, -45).Air(5, 25, -60).Air(-30, 14, -40).Air(-21, 5, -16.4f),
            Speed = 6f, Warn = true, TurnSharpness = 2f,
            Audio = new AudioSpec { Loop = HoverHumLoop, Depart = ThrusterBurst, WaitingVolume = 0.15f, WaitingPitch = 0.7f, MinDistance = 4f, MaxDistance = 45f, Priority = 90 },
        });

        scene.Add(new MoverSpec
        {
            Name = "UFO", Build = BuildUfo,
            Route = new RouteSpec("UFO Flight")
                .Air(60, 30, 10).Air(85, 45, 30).Air(70, 55, 50).Air(40, 35, 70).Air(21, 8, 84).Wait(6)
                .Air(10, 25, 60).Air(-20, 40, 30).Air(5, 32, -10).Air(35, 22, -5).Air(55, 38, -15),
            Speed = 14f, Bob = 0.8f, BobFrequency = 1.2f, Warn = false, TurnSharpness = 3f,
            Audio = new AudioSpec { Loop = UfoHumLoop, WaitingVolume = 0.8f, WaitingPitch = 0.9f, MinDistance = 6f, MaxDistance = 70f, Priority = 100 },
        });

        scene.Add(new MoverSpec
        {
            Name = "Floating Rocks", Existing = "Floating Rocks/RockFloating01 (1)",
            Route = new RouteSpec("Rock Drift") { Relative = true }.Circle(new Vector3(-1.5f, 0, 0), 1.5f, 8, false),
            Speed = 0.25f, Bob = 1f, BobFrequency = 0.08f, Warn = false,
            Audio = new AudioSpec { Loop = HoverHumLoop, MovingVolume = 0.4f, MovingPitch = 0.45f, MaxDistance = 25f, Priority = 200 },
        });

        // A small flying alien critter darting around the customer launch pad
        scene.Add(new MoverSpec
        {
            Name = "Alien Critter", Asset = MonsterPets + "Hywirl.glb", Size = 1.2f,
            Route = new RouteSpec("Critter Flight")
                .Air(27, -1, -9).Air(33, 1, -5).Air(39, -0.5f, -10).Air(38, 2, -18).Air(31, 0, -21).Air(26, 1.5f, -16),
            Speed = 3f, Bob = 0.5f, BobFrequency = 0.7f, Warn = false, YawOnly = false,
            ClipModel = MonsterPets + "Hywirl.glb", IdleClips = Hovering, MoveClips = Flying,
            Audio = new AudioSpec { Occasional = new[] { AlienChirp }, Interval = 8f, MaxDistance = 15f },
        });

        // A big creature prowling the plain between the repair shop and the control tower
        scene.Add(new MoverSpec
        {
            Name = "Alien Beast", Asset = MonsterPets + "Dino.glb", Size = 3.5f,
            Route = new RouteSpec("Beast Prowl")
                .Ground(38, -2, 15).Ground(55, -2, 22).Wait(5).Ground(60, -2, 5).Ground(50, -2, -1).Wait(5).Ground(54, -2, 10),
            Speed = 1.5f, Warn = true,
            ClipModel = MonsterPets + "Dino.glb", IdleClips = Standing, MoveClips = Walking,
            Audio = new AudioSpec { Loop = FootstepsLoop, Occasional = new[] { CreatureGrowl }, Interval = 20f, WaitingVolume = 0f, MovingPitch = 0.6f, MaxDistance = 25f },
        });
        return scene;
    }

    // A flying saucer from built-in shapes: a silver hull with a box collider, a glowing cyan dome and a yellow light ring
    private static GameObject BuildUfo(Transform root)
    {
        const string prefabPath = MoverFolder + "UFO.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            GameObject ufo = new GameObject("UFO");
            GameObject hull = Shape(PrimitiveType.Sphere, ufo.transform, "UFO", new Vector3(6f, 0.9f, 6f), Vector3.zero,
                MakeMaterial("UFO Hull", new Color(0.72f, 0.75f, 0.8f), Color.black, 0.8f));
            Shape(PrimitiveType.Sphere, ufo.transform, "Dome", new Vector3(2.4f, 1.6f, 2.4f), new Vector3(0f, 0.45f, 0f),
                MakeMaterial("UFO Dome", new Color(0.4f, 0.9f, 1f), new Color(0.1f, 0.45f, 0.55f), 0.2f));
            Shape(PrimitiveType.Cylinder, ufo.transform, "Light Ring", new Vector3(6.4f, 0.06f, 6.4f), Vector3.zero,
                MakeMaterial("UFO Lights", new Color(1f, 0.9f, 0.35f), new Color(1.6f, 1.3f, 0.3f), 0f));
            AddNamedCollider(hull.GetComponent<Renderer>(), "UFO");
            prefab = PrefabUtility.SaveAsPrefabAsset(ufo, prefabPath);
            Object.DestroyImmediate(ufo);
        }
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
    }

    private static GameObject Shape(PrimitiveType type, Transform parent, string name, Vector3 scale, Vector3 position, Material material)
    {
        GameObject shape = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(shape.GetComponent<Collider>());
        shape.name = name;
        shape.transform.SetParent(parent, false);
        shape.transform.localScale = scale;
        shape.transform.localPosition = position;
        Renderer renderer = shape.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return shape;
    }

    private static Material MakeMaterial(string name, Color color, Color emission, float metallic)
    {
        string path = $"{MoverFolder}Materials/{name}.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;

        material = new Material(Shader.Find("Standard")) { color = color, enableInstancing = true };
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Glossiness", 0.6f);
        if (emission != Color.black)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emission);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    // ---------- Fantasy bog ----------

    private static SceneSpec WitchCottage()
    {
        SceneSpec scene = new SceneSpec("Witch Cottage");

        // The whole cluster of floating potions, flasks and cauldrons slowly swirls and bobs in the air
        scene.Add(new MoverSpec
        {
            Name = "Floating Potions, Flasks, and Cauldrons", Existing = "Cottage Curiosities/Floating Potions, Flasks, and Cauldrons",
            Route = new RouteSpec("Potion Swirl") { Relative = true }.Circle(new Vector3(-2.5f, 0, 0), 2.5f, 8, false),
            Speed = 0.35f, Bob = 0.5f, BobFrequency = 0.15f, Warn = false,
            Audio = new AudioSpec { Loop = MagicShimmerLoop, MinDistance = 3f, MaxDistance = 18f },
        });

        scene.Add(new MoverSpec
        {
            Name = "Flying Broom", Build = BuildBroom,
            Route = new RouteSpec("Broom Flight").Air(16, 18, 0).Air(10, 21, 13).Air(-4, 17, 17).Air(-16, 20, 4).Air(-12, 17, -12).Air(2, 21, -16).Air(13, 17, -10),
            Speed = 6f, Bob = 0.6f, BobFrequency = 0.6f, Warn = false, YawOnly = false,
            Audio = new AudioSpec { Loop = BroomWhooshLoop, MaxDistance = 25f },
        });

        scene.Add(new MoverSpec
        {
            Name = "Ghost", Asset = MonsterPets + "Ghost.glb", Size = 1.8f,
            Route = new RouteSpec("Ghost Drift").Air(31, 7.5f, 0).Air(40, 8.5f, -4).Air(47, 7, 6).Air(45, 9, 20).Air(36, 7.5f, 24).Air(30, 8, 12),
            Speed = 1.8f, Bob = 0.4f, BobFrequency = 0.4f, Warn = false,
            ClipModel = MonsterPets + "Ghost.glb", IdleClips = Hovering, MoveClips = Flying,
            Audio = new AudioSpec { Occasional = new[] { GhostMoan }, Interval = 20f, MaxDistance = 25f },
        });
        return scene;
    }

    // A witch's broom from built-in shapes (the project only has a modern push broom): a long wooden handle pointing forward,
    // a bundle of straw bristles trailing behind, and a dark band binding them
    private static GameObject BuildBroom(Transform root)
    {
        const string prefabPath = MoverFolder + "Witch Broom.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            GameObject broom = new GameObject("Witch Broom");
            GameObject handle = Shape(PrimitiveType.Cylinder, broom.transform, "Flying Broom", new Vector3(0.07f, 0.8f, 0.07f), new Vector3(0f, 0f, 0.25f),
                MakeMaterial("Broom Handle", new Color(0.42f, 0.27f, 0.14f), Color.black, 0f));
            handle.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Shape(PrimitiveType.Sphere, broom.transform, "Bristles", new Vector3(0.38f, 0.32f, 0.75f), new Vector3(0f, 0f, -0.75f),
                MakeMaterial("Broom Bristles", new Color(0.85f, 0.7f, 0.35f), Color.black, 0f));
            GameObject band = Shape(PrimitiveType.Cylinder, broom.transform, "Band", new Vector3(0.2f, 0.05f, 0.2f), new Vector3(0f, 0f, -0.42f),
                MakeMaterial("Broom Band", new Color(0.2f, 0.12f, 0.25f), Color.black, 0f));
            band.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            AddNamedCollider(handle.GetComponent<Renderer>(), "Flying Broom");
            prefab = PrefabUtility.SaveAsPrefabAsset(broom, prefabPath);
            Object.DestroyImmediate(broom);
        }
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
    }

    // ---------- Modern city ----------
    // Roads are 6m wide; vehicles keep to the right-hand lane 1.6m from the centre line, so traffic in opposite directions never overlaps

    private static SceneSpec Kitchen()
    {
        SceneSpec scene = new SceneSpec("Kitchen");

        // Bus: up the x=21.7 road, turns at the north junction, stops at the bus stop, then loops round the west block
        scene.Add(new MoverSpec
        {
            Name = "City Bus", Asset = MoverFolder + "City Bus.prefab",
            Route = new RouteSpec("Bus Route")
                .Ground(8, 5, 33.9f).Ground(18, 5, 33.9f).Ground(23.3f, 5, 38).Ground(23.3f, 5, 55.5f)
                .Ground(22.5f, 5, 60.8f).Ground(20.1f, 5, 56).Ground(20.1f, 5, 51.6f).Wait(8)
                .Ground(20.1f, 5, 38).Ground(20.1f, 5, -20.5f).Ground(17, 5, -22.7f).Ground(7.5f, 5, -22.7f).Ground(5.8f, 5, -19).Ground(5.8f, 5, 30),
            Speed = 6f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Yellow Sports Car", Asset = MoverFolder + "Yellow Sports Car.prefab",
            Route = new RouteSpec("Car Loop")
                .Ground(2.6f, 5, 30).Ground(2.6f, 5, -20).Ground(0, 5, -22.7f).Ground(-40, 5, -22.7f).Ground(-42.6f, 5, -20)
                .Ground(-42.6f, 5, 31).Ground(-40, 5, 33.9f).Ground(0, 5, 33.9f),
            Speed = 7f, CycleOffset = 0.4f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Blue Van", Asset = MoverFolder + "Blue Van.prefab",
            Route = new RouteSpec("Delivery Run", loop: false)
                .Ground(12, 5, 58).Wait(10).Ground(-10, 5, 58).Ground(-34, 5, 58).Wait(10),
            Speed = 5f, TurnSharpness = 1.5f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Woman in Red Shirt", Asset = MoverFolder + "Woman in Red Shirt.prefab",
            Route = new RouteSpec("Sidewalk Jog", loop: false).Ground(8.2f, 5, -18).Wait(4).Ground(8.2f, 5, 28).Wait(4),
            Speed = 2.4f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Man in Gray Shirt", Asset = MoverFolder + "Man in Gray Shirt.prefab",
            Route = new RouteSpec("North Sidewalk Walk", loop: false).Ground(-38, 5, 39.5f).Wait(5).Ground(-18, 5, 39.5f).Ground(0, 5, 39.5f).Wait(5),
            Speed = 1.3f,
        });
        // The corgi walks alongside him, as if he's out walking his dog
        scene.Add(new MoverSpec
        {
            Name = "Corgi", Asset = MoverFolder + "Corgi.prefab",
            Route = new RouteSpec("Dog Walk", loop: false).Ground(-38, 5, 40.4f).Wait(5).Ground(-18, 5, 40.4f).Ground(0, 5, 40.4f).Wait(5),
            Speed = 1.3f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Sparrow", Asset = SparrowPrefab,
            Route = new RouteSpec("Pond Sparrow").Air(-12, 10, 40).Air(0, 13, 52).Air(8, 9, 45).Ground(-1, 5, 39).Wait(5).Air(-10, 7, 47),
        });
        return scene;
    }

    private static SceneSpec Pharmacy()
    {
        SceneSpec scene = new SceneSpec("Pharmacy");

        // Bus: anticlockwise round the east loop (x 22-49, z -36-65), stopping by the houses on the way south
        scene.Add(new MoverSpec
        {
            Name = "City Bus", Asset = MoverFolder + "City Bus.prefab",
            Route = new RouteSpec("Bus Route")
                .Ground(25, 0, -37.6f).Ground(47, 0, -37.6f).Ground(50.6f, 0, -34).Ground(50.6f, 0, 62).Ground(47, 0, 66.6f)
                .Ground(25, 0, 66.6f).Ground(20.4f, 0, 62).Ground(20.4f, 0, 15).Wait(8).Ground(20.4f, 0, -34),
            Speed = 6f,
        });
        // Van: clockwise round the loop that surrounds the pharmacy
        scene.Add(new MoverSpec
        {
            Name = "Blue Van", Asset = MoverFolder + "Blue Van.prefab",
            Route = new RouteSpec("Pharmacy Loop")
                .Ground(-17, 0, 20.3f).Ground(11, 0, 20.3f).Ground(13.9f, 0, 17).Ground(13.9f, 0, -33)
                .Ground(11, 0, -34.9f).Ground(-16, 0, -34.9f).Ground(-19.2f, 0, -32).Ground(-19.2f, 0, 17),
            Speed = 5f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Woman in Red Shirt", Asset = MoverFolder + "Woman in Red Shirt.prefab",
            Route = new RouteSpec("Sidewalk Jog", loop: false).Ground(25.2f, 0, -32).Wait(4).Ground(25.2f, 0, 35).Wait(4),
            Speed = 2.4f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Man in Gray Shirt", Asset = MoverFolder + "Man in Gray Shirt.prefab",
            Route = new RouteSpec("North Sidewalk Walk", loop: false).Ground(-55, 0, 52.5f).Wait(5).Ground(-20, 0, 52.5f).Ground(8, 0, 52.5f).Wait(5),
            Speed = 1.3f,
        });
        scene.Add(new MoverSpec
        {
            Name = "Sparrow", Asset = SparrowPrefab,
            Route = new RouteSpec("Park Sparrow").Air(-60, 10, -50).Air(-45, 13, -42).Air(-35, 9, -58).Ground(-47, 0, -60).Wait(6).Air(-58, 8, -64),
        });
        return scene;
    }
}
