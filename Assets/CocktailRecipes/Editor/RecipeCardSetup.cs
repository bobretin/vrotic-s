using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// One-shot builder for the cocktail recipe flip UI on the bar counter cube.
/// Called from the Unity CLI eval: RecipeCardSetup.Inspect() / RecipeCardSetup.Build()
/// </summary>
public static class RecipeCardSetup
{
    const string TexDir = "Assets/CocktailRecipes/Textures";
    const string FontDir = "Assets/CocktailRecipes/Fonts";

    static readonly (string file, string title, string ingredients)[] Data =
    {
        ("cocktail_aurora", "Аврора", "Красная жидкость + синяя жидкость + лёд"),
        ("cocktail_lagoon", "Синяя лагуна", "Синяя жидкость + прозрачная жидкость + лёд"),
        ("cocktail_sunset", "Закат", "Жёлтая жидкость + красная жидкость + лёд"),
    };

    public static string Inspect()
    {
        var scene = SceneManager.GetActiveScene();
        var cubes = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.gameObject.scene == scene && t.name.StartsWith("Cube"))
            .Select(t =>
            {
                var r = t.GetComponent<Renderer>();
                string b = r != null ? r.bounds.ToString("F3") + " size=" + r.bounds.size.ToString("F3") : "no-renderer";
                return $"name={t.name} path={PathOf(t)} pos={t.position:F3} scale={t.lossyScale:F3} {b}";
            });
        var roots = scene.GetRootGameObjects().Select(g => g.name);
        return "SCENE=" + scene.name + " roots=[" + string.Join(", ", roots) + "]\n" + string.Join("\n", cubes);
    }

    public static string Build()
    {
        var scene = SceneManager.GetActiveScene();

        // 1. Textures -> sprites
        foreach (var d in Data)
        {
            string p = $"{TexDir}/{d.file}.png";
            var imp = TextureImporter.GetAtPath(p) as TextureImporter;
            if (imp == null) return "FAIL: texture not found " + p;
            if (imp.textureType != TextureImporterType.Sprite)
            {
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
        }

        // 2. Cyrillic-capable dynamic TMP font (from Windows Arial)
        Directory.CreateDirectory(FontDir);
        string ttf = FontDir + "/arial.ttf";
        if (!File.Exists(ttf))
        {
            File.Copy(@"C:\Windows\Fonts\arial.ttf", ttf);
            AssetDatabase.ImportAsset(ttf);
        }
        var font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
        if (font == null) return "FAIL: font not imported";
        string fontPath = FontDir + "/ArialCyrillic.asset";
        var tmpFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
        if (tmpFont == null)
        {
            tmpFont = TMP_FontAsset.CreateFontAsset(font);
            AssetDatabase.CreateAsset(tmpFont, fontPath);
            if (tmpFont.material != null) AssetDatabase.AddObjectToAsset(tmpFont.material, tmpFont);
            if (tmpFont.atlasTexture != null) AssetDatabase.AddObjectToAsset(tmpFont.atlasTexture, tmpFont);
            AssetDatabase.SaveAssets();
        }

        // 3. Find the bar counter cube (барная стойка/Cube)
        var cube = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.gameObject.scene == scene && t.name.StartsWith("Cube"))
            .OrderByDescending(t => HasAncestorWith(t, "стойка") || HasAncestorWith(t, "бар"))
            .FirstOrDefault();
        if (cube == null) return "FAIL: no Cube found in scene " + scene.name;

        // Remove a previous build if re-running
        var old = GameObject.Find("RecipeCardUI");
        if (old != null) Object.DestroyImmediate(old);

        // 4. Enlarge the cube (user authorized) so the recipe board fits:
        //    it is a flat plate whose normal is local Y; face spans local X (width) and Z (height).
        var mf = cube.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return "FAIL: cube has no mesh";
        Vector3 me = mf.sharedMesh.bounds.extents;
        Vector3 s = cube.lossyScale;
        float faceW = 2f * me.x * s.x;   // world length along cube.right
        float thick = 2f * me.y * s.y;   // world length along cube.up
        float faceH = 2f * me.z * s.z;   // world length along cube.forward
        const float wantW = 0.70f, wantH = 0.88f;
        cube.localScale = new Vector3(
            cube.localScale.x * (wantW / faceW),
            cube.localScale.y,
            cube.localScale.z * (wantH / faceH));
        thick = 2f * me.y * cube.lossyScale.y;

        // Lift the enlarged plate back onto the counter surface (raycast the counter top).
        var rendNow = cube.GetComponent<Renderer>();
        if (rendNow != null)
        {
            float counterTopY = 0.836f; // fallback: original resting height
            var cubeCols = cube.GetComponentsInChildren<Collider>();
            foreach (var c in cubeCols) c.enabled = false;
            if (Physics.Raycast(new Vector3(cube.position.x, 5f, cube.position.z), Vector3.down, out var hit, 20f))
                counterTopY = hit.point.y;
            foreach (var c in cubeCols) c.enabled = true;
            cube.position += Vector3.up * (counterTopY + 0.01f - rendNow.bounds.min.y);
        }

        // 5. Orientation: use the plate's upward-facing side (its natural reading surface).
        Vector3 n = cube.up;
        if (n.y < 0f) n = -n;
        Vector3 upOnPlane = Vector3.up - n * Vector3.Dot(Vector3.up, n);
        if (upOnPlane.sqrMagnitude < 1e-4f) upOnPlane = cube.forward;
        upOnPlane.Normalize();
        // uGUI is visible from the NEGATIVE Z side of the canvas, so forward = -n.
        Quaternion rot = Quaternion.LookRotation(-n, upOnPlane);

        var rend = cube.GetComponent<Renderer>();
        Vector3 center = rend != null ? rend.bounds.center : cube.position;
        Vector3 uiPos = center + n * (thick / 2f + 0.008f);

        // 6. Canvas (world space), sized to the plate face
        float canvasW = wantW * 0.95f, canvasH = wantH * 0.95f;
        var uiGO = new GameObject("RecipeCardUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Undo.RegisterCreatedObjectUndo(uiGO, "Recipe Card UI");
        var rt = (RectTransform)uiGO.transform;
        rt.position = uiPos;
        rt.rotation = rot;
        rt.localScale = Vector3.one * 0.001f; // sizeDelta is in mm -> world meters
        rt.sizeDelta = new Vector2(canvasW / 0.001f, canvasH / 0.001f);
        uiGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        // 7. Card pivot (the part that flips) — upper ~88% of the board
        var card = NewRect("Card", rt);
        card.anchorMin = new Vector2(0f, 0.12f);
        card.anchorMax = new Vector2(1f, 1f);
        card.offsetMin = card.offsetMax = Vector2.zero;

        var bg = NewImage("Background", card, new Color(0.08f, 0.06f, 0.10f, 0.92f));
        Stretch(bg.rectTransform);

        var img = NewImage("CocktailImage", card, Color.white);
        var imgRt = img.rectTransform;
        imgRt.anchorMin = new Vector2(0.06f, 0.30f);
        imgRt.anchorMax = new Vector2(0.94f, 0.98f);
        imgRt.offsetMin = imgRt.offsetMax = Vector2.zero;
        img.preserveAspect = true;

        var title = NewText("Title", card, tmpFont, Data[0].title, 40, FontStyles.Bold, TextAlignmentOptions.Center);
        var titleRt = title.rectTransform;
        titleRt.anchorMin = new Vector2(0.05f, 0.20f);
        titleRt.anchorMax = new Vector2(0.95f, 0.30f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        var ingr = NewText("Ingredients", card, tmpFont, Data[0].ingredients, 28, FontStyles.Normal, TextAlignmentOptions.Center);
        ingr.color = new Color(0.92f, 0.88f, 0.80f, 1f);
        var ingrRt = ingr.rectTransform;
        ingrRt.anchorMin = new Vector2(0.05f, 0.02f);
        ingrRt.anchorMax = new Vector2(0.95f, 0.19f);
        ingrRt.offsetMin = ingrRt.offsetMax = Vector2.zero;

        // 8. Buttons (static, bottom strip — outside the flipping card)
        var prevBtn = NewButton("PrevButton", rt, tmpFont, "← Назад", new Vector2(0.02f, 0.005f), new Vector2(0.34f, 0.105f));
        var nextBtn = NewButton("NextButton", rt, tmpFont, "Вперёд →", new Vector2(0.66f, 0.005f), new Vector2(0.98f, 0.105f));

        // 9. Flipper component + wiring
        var flipper = uiGO.AddComponent<RecipeCardFlipper>();
        flipper.cardPivot = card;
        flipper.cocktailImage = img;
        flipper.titleText = title;
        flipper.ingredientsText = ingr;
        flipper.recipes = Data.Select(d => new RecipeCardFlipper.Recipe
        {
            title = d.title,
            ingredients = d.ingredients,
            image = AssetDatabase.LoadAssetAtPath<Sprite>($"{TexDir}/{d.file}.png"),
        }).ToArray();
        if (flipper.recipes.Any(r => r.image == null)) return "FAIL: sprite load failed";

        // Show the first recipe already in edit mode
        img.sprite = flipper.recipes[0].image;

        UnityEventTools.AddVoidPersistentListener(prevBtn.onClick, flipper.Prev);
        UnityEventTools.AddVoidPersistentListener(nextBtn.onClick, flipper.Next);

        // 10. Save scene + assets
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        return $"OK: cube '{PathOf(cube)}' scaled to {wantW}x{wantH} m, UI at {uiPos}";
    }

    static bool HasAncestorWith(Transform t, string frag)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name.ToLowerInvariant().Contains(frag)) return true;
        return false;
    }

    static string PathOf(Transform t)
    {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static Image NewImage(string name, Transform parent, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = c;
        return img;
    }

    static TextMeshProUGUI NewText(string name, Transform parent, TMP_FontAsset font, string text, float size, FontStyles style, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = Color.white;
        t.enableWordWrapping = true;
        return t;
    }

    static Button NewButton(string name, Transform parent, TMP_FontAsset font, string label, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.GetComponent<Image>();
        img.color = new Color(0.25f, 0.18f, 0.35f, 0.95f);
        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        var txt = NewText("Text", rt, font, label, 26, FontStyles.Bold, TextAlignmentOptions.Center);
        Stretch(txt.rectTransform);
        return btn;
    }
}
