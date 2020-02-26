using UnityEngine;
using UnityEditor;

public class ImportPlayerPrefsWizard : ScriptableWizard
{
    // Company and product name for importing PlayerPrefs from other projects
    [SerializeField] private string importCompanyName = "";
    [SerializeField] private string importProductName = "";

    private void OnEnable()
    {
        importCompanyName = PlayerSettings.companyName;
        importProductName = PlayerSettings.productName;
    }

    private void OnInspectorUpdate()
    {
        if (Resources.FindObjectsOfTypeAll(typeof(PlayerPrefsManagerEditor)).Length == 0)
        {
            Close();
        }
    }

    protected override bool DrawWizardGUI()
    {
        GUILayout.Label("Import PlayerPrefs from another project, also useful if you change product or company name", EditorStyles.wordWrappedLabel);
        EditorGUILayout.Separator();
        bool v = base.DrawWizardGUI();
        return v;
    }

    private void OnWizardCreate()
    {
        if (Resources.FindObjectsOfTypeAll(typeof(PlayerPrefsManagerEditor)).Length >= 1)
        {
            ((PlayerPrefsManagerEditor)Resources.FindObjectsOfTypeAll(typeof(PlayerPrefsManagerEditor))[0]).Import(importCompanyName, importProductName);
        }
    }
}
