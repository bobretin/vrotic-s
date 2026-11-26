using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Flips through cocktail recipe cards with a card-flip animation.
/// Hook Next()/Prev() to UI buttons (works with XR UI input too).
/// </summary>
public class RecipeCardFlipper : MonoBehaviour
{
    [System.Serializable]
    public class Recipe
    {
        public string title;
        public Sprite image;
        [TextArea(1, 3)] public string ingredients;
    }

    [Header("Data")]
    public Recipe[] recipes;

    [Header("References")]
    public RectTransform cardPivot;
    public Image cocktailImage;
    public TMP_Text titleText;
    public TMP_Text ingredientsText;

    [Header("Animation")]
    public float halfFlipDuration = 0.22f;

    int index;
    bool flipping;

    void Start()
    {
        if (recipes != null && recipes.Length > 0)
            Apply();
    }

    public void Next() { TryFlip(1); }
    public void Prev() { TryFlip(-1); }

    void TryFlip(int dir)
    {
        if (flipping || recipes == null || recipes.Length == 0)
            return;
        StartCoroutine(FlipRoutine(dir));
    }

    IEnumerator FlipRoutine(int dir)
    {
        flipping = true;

        // First half: rotate the card edge-on.
        for (float t = 0f; t < halfFlipDuration; t += Time.deltaTime)
        {
            float k = EaseInOut(t / halfFlipDuration);
            cardPivot.localEulerAngles = new Vector3(0f, Mathf.Lerp(0f, 90f * dir, k), 0f);
            yield return null;
        }

        index = (index + dir + recipes.Length) % recipes.Length;
        Apply();

        // Second half: rotate the new card back into view.
        for (float t = 0f; t < halfFlipDuration; t += Time.deltaTime)
        {
            float k = EaseInOut(t / halfFlipDuration);
            cardPivot.localEulerAngles = new Vector3(0f, Mathf.Lerp(-90f * dir, 0f, k), 0f);
            yield return null;
        }

        cardPivot.localEulerAngles = Vector3.zero;
        flipping = false;
    }

    void Apply()
    {
        Recipe r = recipes[index];
        if (cocktailImage != null) cocktailImage.sprite = r.image;
        if (titleText != null) titleText.text = r.title;
        if (ingredientsText != null) ingredientsText.text = r.ingredients;
    }

    static float EaseInOut(float t)
    {
        return t * t * (3f - 2f * t);
    }
}
