using System.IO;
using Shopkeeper.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shopkeeper.EditorTools
{
    /// <summary>
    /// Generates Assets/Scenes/ShopDemo.unity: a small shop set made from primitives, the puppet, and the
    /// demo controller. Rerunnable; also callable headless:
    ///   Unity -batchmode -projectPath . -executeMethod Shopkeeper.EditorTools.DemoSceneBuilder.BuildFromCommandLine -quit
    /// </summary>
    public static class DemoSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/ShopDemo.unity";
        const string TemplateScene = "Assets/Scenes/SampleScene.unity";
        const string MatDir = "Assets/Shopkeeper/Art/Materials";

        [MenuItem("Shopkeeper/Build Demo Scene", priority = 0)]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
            EditorUtility.DisplayDialog("Shopkeeper", "Built " + ScenePath + ".\nStart the proxy (python proxy/server.py) and press Play.", "OK");
        }

        public static void BuildFromCommandLine()
        {
            Build();
            EditorApplication.Exit(0);
        }

        public static void Build()
        {
            // Start from the template scene so we inherit its URP camera, sun and post-processing volume.
            if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            if (File.Exists(TemplateScene)) AssetDatabase.CopyAsset(TemplateScene, ScenePath);
            var scene = File.Exists(ScenePath)
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var cam = Camera.main ? Camera.main : new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" }.GetComponent<Camera>();
            cam.transform.position = new Vector3(0.3f, 1.65f, -5.6f);
            cam.transform.LookAt(new Vector3(0.3f, 1.4f, 0.9f));
            cam.fieldOfView = 37f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.035f, 0.03f);

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                l.color = new Color(1f, 0.82f, 0.62f);
                l.intensity = 0.55f;
                l.transform.rotation = Quaternion.Euler(38f, -28f, 0f);
                l.shadows = LightShadows.Soft;
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.16f, 0.13f);

            Directory.CreateDirectory(MatDir);
            var wood = Mat("Wood", new Color(0.36f, 0.22f, 0.13f), 0.25f);
            var darkWood = Mat("DarkWood", new Color(0.16f, 0.10f, 0.07f), 0.2f);
            var plaster = Mat("Plaster", new Color(0.30f, 0.24f, 0.20f), 0.05f);
            var robe = Mat("Robe", new Color(0.20f, 0.38f, 0.30f), 0.15f);
            var skin = Mat("Skin", new Color(0.93f, 0.74f, 0.60f), 0.35f);
            var ink = Mat("Ink", new Color(0.08f, 0.06f, 0.06f), 0.6f);
            var hatMat = Mat("Hat", new Color(0.42f, 0.17f, 0.36f), 0.2f);
            var brass = Mat("Brass", new Color(0.78f, 0.58f, 0.25f), 0.75f, metallic: 0.9f);
            var glow = Mat("LanternGlow", new Color(1f, 0.75f, 0.35f), 0.9f, emission: new Color(1f, 0.6f, 0.2f) * 4f);
            Material[] jarMats =
            {
                Mat("PotionGreen", new Color(0.3f, 0.8f, 0.4f), 0.9f, emission: new Color(0.2f, 0.9f, 0.3f) * 0.8f),
                Mat("PotionBlue", new Color(0.3f, 0.5f, 0.95f), 0.9f, emission: new Color(0.2f, 0.4f, 1f) * 0.6f),
                Mat("PotionRed", new Color(0.85f, 0.25f, 0.3f), 0.9f),
                Mat("Clay", new Color(0.6f, 0.4f, 0.28f), 0.1f),
            };

            var set = new GameObject("ShopSet").transform;

            // Room.
            Prim(PrimitiveType.Cube, "Floor", set, new Vector3(0, -0.05f, 1.5f), new Vector3(14, 0.1f, 8), darkWood);
            Prim(PrimitiveType.Cube, "BackWall", set, new Vector3(0, 2.5f, 2.6f), new Vector3(14, 6, 0.2f), plaster);
            var rng = new System.Random(7);
            for (int s = 0; s < 3; s++)
            {
                float y = 1.55f + s * 0.8f;
                Prim(PrimitiveType.Cube, "Shelf" + s, set, new Vector3(0.6f, y, 2.35f), new Vector3(8.5f, 0.06f, 0.4f), wood);
                for (float x = -3.4f; x < 4.8f; x += 0.35f + (float)rng.NextDouble() * 0.45f)
                {
                    float h = 0.15f + (float)rng.NextDouble() * 0.3f;
                    var type = rng.NextDouble() < 0.7 ? PrimitiveType.Cylinder : PrimitiveType.Sphere;
                    var size = type == PrimitiveType.Cylinder ? new Vector3(0.18f, h * 0.5f, 0.18f) : Vector3.one * (0.12f + h * 0.4f);
                    float yy = y + 0.03f + (type == PrimitiveType.Cylinder ? h * 0.5f : size.y * 0.5f);
                    Prim(type, "Jar", set, new Vector3(x, yy, 2.35f), size, jarMats[rng.Next(jarMats.Length)]);
                }
            }

            // Counter.
            Prim(PrimitiveType.Cube, "Counter", set, new Vector3(0.2f, 0.5f, 0.2f), new Vector3(6.5f, 1f, 1.1f), wood);
            Prim(PrimitiveType.Cube, "CounterTop", set, new Vector3(0.2f, 1.02f, 0.2f), new Vector3(6.7f, 0.06f, 1.25f), darkWood);

            // Lantern (the item on sale).
            var lantern = new GameObject("Lantern").transform;
            lantern.SetParent(set);
            lantern.position = new Vector3(0.05f, 1.05f, 0.0f);
            Prim(PrimitiveType.Cylinder, "Base", lantern, lantern.position + new Vector3(0, 0.04f, 0), new Vector3(0.36f, 0.04f, 0.36f), brass);
            Prim(PrimitiveType.Sphere, "Glass", lantern, lantern.position + new Vector3(0, 0.26f, 0), new Vector3(0.3f, 0.36f, 0.3f), glow);
            Prim(PrimitiveType.Cylinder, "Cap", lantern, lantern.position + new Vector3(0, 0.47f, 0), new Vector3(0.24f, 0.04f, 0.24f), brass);
            Prim(PrimitiveType.Cylinder, "Handle", lantern, lantern.position + new Vector3(0, 0.56f, 0), new Vector3(0.05f, 0.06f, 0.05f), brass);
            var flame = new GameObject("Flame", typeof(Light)).GetComponent<Light>();
            flame.transform.SetParent(lantern);
            flame.transform.position = lantern.position + new Vector3(0, 0.3f, -0.3f);
            flame.type = LightType.Point;
            flame.color = new Color(1f, 0.65f, 0.3f);
            flame.range = 4.5f;
            flame.intensity = 2.5f;
            flame.shadows = LightShadows.None;
            flame.gameObject.AddComponent<LanternFlicker>();

            // Shopkeeper puppet.
            var root = new GameObject("Grizelda").transform;
            root.SetParent(set);
            root.position = new Vector3(-1.25f, 0f, 0.95f);
            var body = Prim(PrimitiveType.Capsule, "Body", root, root.position + new Vector3(0, 1.15f, 0), new Vector3(0.95f, 0.8f, 0.7f), robe).transform;
            var head = new GameObject("Head").transform;
            head.SetParent(root);
            head.position = root.position + new Vector3(0, 2.12f, 0);
            var face = Prim(PrimitiveType.Sphere, "Face", head, head.position, new Vector3(0.62f, 0.66f, 0.6f), skin);
            Prim(PrimitiveType.Sphere, "Nose", head, head.position + new Vector3(0, -0.02f, -0.31f), new Vector3(0.12f, 0.16f, 0.14f), skin);
            Prim(PrimitiveType.Sphere, "EyeL", head, head.position + new Vector3(-0.12f, 0.07f, -0.27f), Vector3.one * 0.075f, ink);
            Prim(PrimitiveType.Sphere, "EyeR", head, head.position + new Vector3(0.12f, 0.07f, -0.27f), Vector3.one * 0.075f, ink);
            var browL = Pivot("BrowL", head, head.position + new Vector3(-0.12f, 0.15f, -0.27f));
            Prim(PrimitiveType.Cube, "Mesh", browL, browL.position, new Vector3(0.14f, 0.025f, 0.03f), ink);
            var browR = Pivot("BrowR", head, head.position + new Vector3(0.12f, 0.15f, -0.27f));
            Prim(PrimitiveType.Cube, "Mesh", browR, browR.position, new Vector3(0.14f, 0.025f, 0.03f), ink);
            // Mouth halves pivot at the centre so rotating them makes a V (smile) or ^ (frown).
            var mouthL = Pivot("MouthL", head, head.position + new Vector3(0, -0.14f, -0.28f));
            Prim(PrimitiveType.Cube, "Mesh", mouthL, mouthL.position + new Vector3(-0.055f, 0, 0), new Vector3(0.11f, 0.022f, 0.03f), ink);
            var mouthR = Pivot("MouthR", head, head.position + new Vector3(0, -0.14f, -0.28f));
            Prim(PrimitiveType.Cube, "Mesh", mouthR, mouthR.position + new Vector3(0.055f, 0, 0), new Vector3(0.11f, 0.022f, 0.03f), ink);
            Prim(PrimitiveType.Cylinder, "HatBrim", head, head.position + new Vector3(0, 0.3f, 0), new Vector3(0.8f, 0.015f, 0.8f), hatMat);
            Prim(PrimitiveType.Cylinder, "HatTop", head, head.position + new Vector3(0, 0.5f, 0.02f), new Vector3(0.42f, 0.2f, 0.42f), hatMat);
            Prim(PrimitiveType.Cylinder, "HatTip", head, head.position + new Vector3(0, 0.76f, 0.05f), new Vector3(0.22f, 0.1f, 0.22f), hatMat);

            var moodLight = new GameObject("MoodLight", typeof(Light)).GetComponent<Light>();
            moodLight.transform.SetParent(root);
            moodLight.transform.position = root.position + new Vector3(-0.8f, 2.4f, -1.3f);
            moodLight.type = LightType.Point;
            moodLight.range = 3.5f;
            moodLight.intensity = 1.6f;
            moodLight.color = new Color(0.62f, 0.6f, 0.56f);

            var puppet = root.gameObject.AddComponent<ShopkeeperPuppet>();
            puppet.Bind(body, head, browL, browR, mouthL, mouthR, face.GetComponent<Renderer>(), moodLight);

            // Controller.
            var demoGo = new GameObject("ShopDemo");
            var demo = demoGo.AddComponent<ShopDemoController>();
            var so = new SerializedObject(demo);
            so.FindProperty("puppet").objectReferenceValue = puppet;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[Shopkeeper] Built " + ScenePath);
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 worldPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, true);
            go.transform.position = worldPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static Transform Pivot(string name, Transform parent, Vector3 worldPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, true);
            t.position = worldPos;
            return t;
        }

        static Material Mat(string name, Color color, float smoothness, float metallic = 0f, Color? emission = null)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
