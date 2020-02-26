using System;
using System.Collections.Generic;
using System.IO;
using PlistCS;
using UnityEditor;
using UnityEngine;
#if UNITY_5_6_OR_NEWER
using UnityEditor.IMGUI.Controls;
#endif

public class PlayerPrefsManagerEditor : EditorWindow
{
    private static readonly System.Text.Encoding Encoding = new System.Text.UTF8Encoding();

    // Represents a PlayerPref key-value record
    [Serializable]
    private struct PlayerPrefPair
    {
        public string Key { get; set; }

        public object Value { get; set; }
    }

    private readonly DateTime _missingDatetime = new DateTime(1601, 1, 1);

    // If True display EditorPrefs instead of PlayerPrefs
    private bool _showEditorPrefs;

    // Natively PlayerPrefs can be one of these three types
    private enum PlayerPrefType
    {
        Float = 0,
        Int,
        String,
        Bool
    };

    // The actual cached store of PlayerPref records fetched from registry or plist
    private List<PlayerPrefPair> _deserializedPlayerPrefs = new List<PlayerPrefPair>();

    // When a search is in effect the search results are cached in this list
    private List<PlayerPrefPair> _filteredPlayerPrefs = new List<PlayerPrefPair>();

    // Track last successful deserialisation to prevent doing this too often. On OSX this uses the PlayerPrefs file
    // last modified time, on Windows we just poll repeatedly and use this to prevent polling again too soon.
    private DateTime? _lastDeserialization;

    // The view position of the PlayerPrefs scroll view
    private Vector2 _scrollPosition;

    // The scroll position from last frame (used with scrollPosition to detect user scrolling)
    private Vector2 _lastScrollPosition;

    // Prevent OnInspector() forcing a repaint every time it's called
    private int _inspectorUpdateFrame;

    // Automatically attempt to decrypt keys and values that are detected as encrypted
    private bool _automaticDecryption = true;

    // Filter the keys by search
    private string _searchFilter = string.Empty;

    // Because of some issues with deleting from OnGUI, we defer it to OnInspectorUpdate() instead
    private string _keyQueuedForDeletion;

    #region Adding New PlayerPref
    // This is the current type of PlayerPref that the user is about to create
    private PlayerPrefType _newEntryType = PlayerPrefType.String;

    // Whether the PlayerPref should be encrypted
    private bool _newEntryIsEncrypted;

    // The identifier of the new PlayerPref
    private string _newEntryKey = "";

    // Value of the PlayerPref about to be created (must be tracked differently for each type)
    private float _newEntryValueFloat;
    private int _newEntryValueInt;
    private bool _newEntryValueBool;
    private string _newEntryValueString = "";
    #endregion

    #if UNITY_5_6_OR_NEWER
    private SearchField _searchField;
    #endif

    [MenuItem("Unity Tools/Player Prefs Manager")]
    private static void Init()
    {
        // Get existing open window or if none, make a new one:
        PlayerPrefsManagerEditor managerEditor =
            (PlayerPrefsManagerEditor) GetWindow(typeof(PlayerPrefsManagerEditor), false, "Prefs Manager");

        // Require the editor window to be at least 300 pixels wide
        Vector2 minSize = managerEditor.minSize;
        minSize.x = 230;
        managerEditor.minSize = minSize;
    }

    private void OnEnable()
    {
        _searchField = new SearchField();
    }

    private void DeleteAll()
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.DeleteAll();
        }
        else
        {
            PlayerPrefs.DeleteAll();
        }
    }

    private void DeleteKey(string key)
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.DeleteKey(key);
        }
        else
        {
            PlayerPrefs.DeleteKey(key);
        }
    }

    private int GetInt(string key, int defaultValue = 0)
    {
        return _showEditorPrefs ? EditorPrefs.GetInt(key, defaultValue) : PlayerPrefs.GetInt(key, defaultValue);
    }

    private float GetFloat(string key, float defaultValue = 0.0f)
    {
        return _showEditorPrefs ? EditorPrefs.GetFloat(key, defaultValue) : PlayerPrefs.GetFloat(key, defaultValue);
    }

    private string GetString(string key, string defaultValue = "")
    {
        return _showEditorPrefs ? EditorPrefs.GetString(key, defaultValue) : PlayerPrefs.GetString(key, defaultValue);
    }

    private bool GetBool(string key, bool defaultValue = false)
    {
        if (_showEditorPrefs)
        {
            return EditorPrefs.GetBool(key, defaultValue);
        }

        throw new NotSupportedException("PlayerPrefs interface does not natively support bools");
    }

    private void SetInt(string key, int value)
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.SetInt(key, value);
        }
        else
        {
            PlayerPrefs.SetInt(key, value);
        }
    }

    private void SetFloat(string key, float value)
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.SetFloat(key, value);
        }
        else
        {
            PlayerPrefs.SetFloat(key, value);
        }
    }

    private void SetString(string key, string value)
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.SetString(key, value);
        }
        else
        {
            PlayerPrefs.SetString(key, value);
        }
    }

    private void SetBool(string key, bool value)
    {
        if (_showEditorPrefs)
        {
            EditorPrefs.SetBool(key, value);
        }
        else
        {
            throw new NotSupportedException("PlayerPrefs interface does not natively support bools");
        }
    }

    private void Save()
    {
        if (_showEditorPrefs)
        {
            // No Save() method in EditorPrefs
        }
        else
        {
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// This returns an array of the stored PlayerPrefs from the file system (OSX) or registry (Windows), to allow 
    /// us to to look up what's actually in the PlayerPrefs. This is used as a kind of lookup table.
    /// </summary>
    private IEnumerable<PlayerPrefPair> RetrieveSavedPrefs(string companyName, string productName)
    {
        switch (Application.platform)
        {
            case RuntimePlatform.OSXEditor:
            {
                string playerPrefsPath;

                if (_showEditorPrefs)
                {
                    // From Unity Docs: On macOS, EditorPrefs are stored in ~/Library/Preferences/com.unity3d.UnityEditor.plist.
                    string majorVersion = Application.unityVersion.Split('.')[0];
                    // Construct the fully qualified path
                    playerPrefsPath =
                        Path.Combine(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "Library/Preferences"), "com.unity3d.UnityEditor" + majorVersion + ".x.plist");
                }
                else
                {
                    // From Unity Docs: On Mac OS X PlayerPrefs are stored in ~/Library/Preferences folder, in a file named unity.[company name].[product name].plist, where company and product names are the names set up in Project Settings. The same .plist file is used for both Projects run in the Editor and standalone players.

                    // Construct the plist filename from the project's settings
                    string plistFilename = $"unity.{companyName}.{productName}.plist";
                    // Now construct the fully qualified path
                    playerPrefsPath =
                        Path.Combine(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "Library/Preferences"), plistFilename);
                }

                // Parse the PlayerPrefs file if it exists
                if (!File.Exists(playerPrefsPath))
                {
                    return new PlayerPrefPair[0];
                }

                // Parse the plist then cast it to a Dictionary
                object plist = Plist.readPlist(playerPrefsPath);

                Dictionary<string, object> parsed = plist as Dictionary<string, object>;

                // Convert the dictionary data into an array of PlayerPrefPairs
                List<PlayerPrefPair> tempPlayerPrefs = new List<PlayerPrefPair>(parsed.Count);

                foreach (KeyValuePair<string, object> pair in parsed)
                {
                    switch (pair.Value)
                    {
                        case double value:
                        {
                            // Some float values may come back as double, so convert them back to floats
                            tempPlayerPrefs.Add(new PlayerPrefPair {Key = pair.Key, Value = (float) value});
                            break;
                        }
                        case bool _:
                        {
                            // Unity PlayerPrefs API doesn't allow bools, so ignore them
                            break;
                        }
                        default:
                        {
                            tempPlayerPrefs.Add(new PlayerPrefPair {Key = pair.Key, Value = pair.Value});
                            break;
                        }
                    }
                }

                // Return the results
                return tempPlayerPrefs.ToArray();
            }
            case RuntimePlatform.WindowsEditor:
            {
                Microsoft.Win32.RegistryKey registryKey;

                if (_showEditorPrefs)
                {
                    string majorVersion = Application.unityVersion.Split('.')[0];
                    
                    #if UNITY_5_5_OR_NEWER
                    registryKey =
                        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                            "Software\\Unity Technologies\\Unity Editor 5.x");
                    #else
                    registryKey =
                        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                            "Software\\Unity Technologies\\Unity Editor " + majorVersion + ".x");
                    #endif
                }
                else
                {
                    // From Unity docs: On Windows, PlayerPrefs are stored in the registry under HKCU\Software\[company name]\[product name] key,
                    // where company and product names are the names set up in Project Settings.
                    #if UNITY_5_5_OR_NEWER
                    // From Unity 5.5 editor PlayerPrefs moved to a specific location
                    registryKey =
                        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                            "Software\\Unity\\UnityEditor\\" + companyName + "\\" + productName);
                    #else
                     registryKey =
                        Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\" + companyName + "\\" + productName);
                    #endif
                }

                // Parse the registry if the specified registryKey exists
                if (registryKey == null)
                {
                    return new PlayerPrefPair[0];
                }

                // Get an array of what keys (registry value names) are stored
                string[] valueNames = registryKey.GetValueNames();

                // Create the array of the right size to take the saved PlayerPrefs
                PlayerPrefPair[] tempPlayerPrefs = new PlayerPrefPair[valueNames.Length];

                // Parse and convert the registry saved PlayerPrefs into our array
                int i = 0;
                foreach (string valueName in valueNames)
                {
                    string key = valueName;

                    // Remove the _h193410979 style suffix used on PlayerPref keys in Windows registry
                    int index = key.LastIndexOf("_", StringComparison.Ordinal);
                    key = key.Remove(index, key.Length - index);

                    // Get the value from the registry
                    object ambiguousValue = registryKey.GetValue(valueName);

                    // Unfortunately floats will come back as an int (at least on 64 bit) because the float is stored as
                    // 64 bit but marked as 32 bit - which confuses the GetValue() method greatly! 
                    if (ambiguousValue is int)
                    {
                        // If the PlayerPref is not actually an int then it must be a float, this will evaluate to true
                        // (impossible for it to be 0 and -1 at the same time)
                        if (GetInt(key, -1) == -1 && GetInt(key, 0) == 0)
                        {
                            // Fetch the float value from PlayerPrefs in memory
                            ambiguousValue = GetFloat(key);
                        }
                        else if (_showEditorPrefs && (GetBool(key, true) != true || GetBool(key, false) != false))
                        {
                            // If it reports a non default value as a bool, it's a bool not a string
                            ambiguousValue = GetBool(key);
                        }
                    }
                    else if (ambiguousValue.GetType() == typeof(byte[]))
                    {
                        // On Unity 5 a string may be stored as binary, so convert it back to a string
                        ambiguousValue = Encoding.GetString((byte[]) ambiguousValue).TrimEnd('\0');
                    }

                    // Assign the key and value into the respective record in our output array
                    tempPlayerPrefs[i] = new PlayerPrefPair() {Key = key, Value = ambiguousValue};
                    i++;
                }

                // Return the results
                return tempPlayerPrefs;
            }
            default:
            {
                throw new NotSupportedException("PlayerPrefsEditor doesn't support this Unity Editor platform");
            }
        }
    }

    private void UpdateSearch()
    {
        // Clear any existing cached search results
        _filteredPlayerPrefs.Clear();

        // Don't attempt to find the search results if a search filter hasn't actually been supplied
        if (string.IsNullOrEmpty(_searchFilter))
        {
            return;
        }

        int entryCount = _deserializedPlayerPrefs.Count;

        // Iterate through all the cached results and add any matches to filteredPlayerPrefs
        for (int i = 0; i < entryCount; i++)
        {
            string fullKey = _deserializedPlayerPrefs[i].Key;
            string displayKey = fullKey;

            // Special case for encrypted keys in auto decrypt mode, search should use decrypted values
            bool isEncryptedPair = PlayerPrefsUtility.IsEncryptedKey(_deserializedPlayerPrefs[i].Key);
            if (_automaticDecryption && isEncryptedPair)
            {
                displayKey = PlayerPrefsUtility.DecryptKey(fullKey);
            }

            // If the key contains the search filter (ToLower used on both parts to make this case insensitive)
            if (displayKey.ToLower().Contains(_searchFilter.ToLower()))
            {
                _filteredPlayerPrefs.Add(_deserializedPlayerPrefs[i]);
            }
        }
    }

    private void DrawTopBar()
    {
        #if UNITY_5_6_OR_NEWER
        string newSearchFilter = _searchField.OnGUI(_searchFilter);
        GUILayout.Space(4);
        #else
        EditorGUILayout.BeginHorizontal();
        // Heading
		GUILayout.Label("Search", GUILayout.MaxWidth(50));
		// Actual search box
        string newSearchFilter = EditorGUILayout.TextField(searchFilter);

        EditorGUILayout.EndHorizontal();
        #endif

        // If the requested search filter has changed
        if (newSearchFilter != _searchFilter)
        {
            _searchFilter = newSearchFilter;
            // Trigger UpdateSearch to calculate new search results
            UpdateSearch();
        }

        // Allow the user to toggle between editor and PlayerPrefs
        int oldIndex = _showEditorPrefs ? 1 : 0;
        int newIndex = GUILayout.Toolbar(oldIndex, new string[] {"PlayerPrefs", "EditorPrefs"});

        // Has the toggle changed?
        if (newIndex != oldIndex)
        {
            // Reset 
            _lastDeserialization = null;
            _showEditorPrefs = (newIndex == 1);
        }
    }

    private void DrawMainList()
    {
        // The bold table headings
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Key", EditorStyles.boldLabel);
        GUILayout.Label("Value", EditorStyles.boldLabel);
        GUILayout.Label("Type", EditorStyles.boldLabel, GUILayout.Width(37));
        GUILayout.Label("Del", EditorStyles.boldLabel, GUILayout.Width(25));
        EditorGUILayout.EndHorizontal();

        // Create a GUIStyle that can be manipulated for the various text fields
        GUIStyle textFieldStyle = new GUIStyle(GUI.skin.textField);

        // Could be dealing with either the full list or search results, so get the right list
        List<PlayerPrefPair> activePlayerPrefs = _deserializedPlayerPrefs;

        if (!string.IsNullOrEmpty(_searchFilter))
        {
            activePlayerPrefs = _filteredPlayerPrefs;
        }

        // Cache the entry count
        int entryCount = activePlayerPrefs.Count;

        // Record the last scroll position so we can calculate if the user has scrolled this frame
        _lastScrollPosition = _scrollPosition;

        // Start the scrollable area
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        // Ensure the scroll doesn't go below zero
        if (_scrollPosition.y < 0)
        {
            _scrollPosition.y = 0;
        }

        // The following code has been optimised so that rather than attempting to draw UI for every single PlayerPref
        // it instead only draws the UI for those currently visible in the scroll view and pads above and below those
        // results to maintain the right size using GUILayout.Space(). This enables us to work with thousands of 
        // PlayerPrefs without slowing the interface to a halt.

        // Fixed height of one of the rows in the table
        float rowHeight = 18;

        // Determine how many rows are visible on screen. For simplicity, use Screen.height (the overhead is negligible)
        int visibleCount = Mathf.CeilToInt(Screen.height / rowHeight);

        // Determine the index of the first PlayerPref that should be drawn as visible in the scrollable area
        int firstShownIndex = Mathf.FloorToInt(_scrollPosition.y / rowHeight);

        // Determine the bottom limit of the visible PlayerPrefs (last shown index + 1)
        int shownIndexLimit = firstShownIndex + visibleCount;

        // If the actual number of PlayerPrefs is smaller than the caculated limit, reduce the limit to match
        if (entryCount < shownIndexLimit)
        {
            shownIndexLimit = entryCount;
        }

        // If the number of displayed PlayerPrefs is smaller than the number we can display (like we're at the end
        // of the list) then move the starting index back to adjust
        if (shownIndexLimit - firstShownIndex < visibleCount)
        {
            firstShownIndex -= visibleCount - (shownIndexLimit - firstShownIndex);
        }

        // Can't have a negative index of a first shown PlayerPref, so clamp to 0
        if (firstShownIndex < 0)
        {
            firstShownIndex = 0;
        }

        // Pad above the on screen results so that we're not wasting draw calls on invisible UI and the drawn player
        // prefs end up in the same place in the list
        GUILayout.Space(firstShownIndex * rowHeight);

        // For each of the on screen results
        for (int i = firstShownIndex; i < shownIndexLimit; i++)
        {
            // Detect if it's an encrypted PlayerPref (these have key prefixes)
            bool isEncryptedPair = PlayerPrefsUtility.IsEncryptedKey(activePlayerPrefs[i].Key);

            // Colour code encrypted PlayerPrefs blue
            if (isEncryptedPair)
            {
                if (UsingProSkin)
                {
                    textFieldStyle.normal.textColor = new Color(0.5f, 0.5f, 1);
                    textFieldStyle.focused.textColor = new Color(0.5f, 0.5f, 1);
                }
                else
                {
                    textFieldStyle.normal.textColor = new Color(0, 0, 1);
                    textFieldStyle.focused.textColor = new Color(0, 0, 1);
                }
            }
            else
            {
                // Normal PlayerPrefs are just black
                textFieldStyle.normal.textColor = GUI.skin.textField.normal.textColor;
                textFieldStyle.focused.textColor = GUI.skin.textField.focused.textColor;
            }

            // The full key is the key that's actually stored in PlayerPrefs
            string fullKey = activePlayerPrefs[i].Key;

            // Display key is used so in the case of encrypted keys, we display the decrypted version instead (in
            // auto-decrypt mode).
            string displayKey = fullKey;

            // Used for accessing the type information stored against the PlayerPref
            object deserializedValue = activePlayerPrefs[i].Value;

            // Track whether the auto decrypt failed, so we can instead fallback to encrypted values and mark it red
            bool failedAutoDecrypt = false;

            // If this is an encrypted play pref and we're attempting to decrypt them, try to decrypt it!
            if (isEncryptedPair && _automaticDecryption)
            {
                // This may throw exceptions (e.g. if private key changes), so wrap in a try-catch
                try
                {
                    deserializedValue = PlayerPrefsUtility.GetEncryptedValue(fullKey, (string) deserializedValue);
                    displayKey = PlayerPrefsUtility.DecryptKey(fullKey);
                }
                catch
                {
                    // Change the colour to red to highlight the decrypt failed
                    textFieldStyle.normal.textColor = Color.red;
                    textFieldStyle.focused.textColor = Color.red;

                    // Track that the auto decrypt failed, so we can prevent any editing 
                    failedAutoDecrypt = true;
                }
            }

            EditorGUILayout.BeginHorizontal();

            // The type of PlayerPref being stored (in auto decrypt mode this works with the decrypted values too)
            Type valueType;

            // If it's an encrypted playerpref, we're automatically decrypting and it didn't fail the earlier 
            // auto decrypt test
            if (isEncryptedPair && _automaticDecryption && !failedAutoDecrypt)
            {
                // Get the encrypted string
                string encryptedValue = GetString(fullKey);
                // Set valueType appropiately based on which type identifier prefix the encrypted string starts with
                if (encryptedValue.StartsWith(PlayerPrefsUtility.VALUE_FLOAT_PREFIX))
                {
                    valueType = typeof(float);
                }
                else if (encryptedValue.StartsWith(PlayerPrefsUtility.VALUE_INT_PREFIX))
                {
                    valueType = typeof(int);
                }
                else if (encryptedValue.StartsWith(PlayerPrefsUtility.VALUE_BOOL_PREFIX))
                {
                    valueType = typeof(bool);
                }
                else if (encryptedValue.StartsWith(PlayerPrefsUtility.VALUE_STRING_PREFIX) ||
                         string.IsNullOrEmpty(encryptedValue))
                {
                    // Special case here, empty encrypted values will also report as strings
                    valueType = typeof(string);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Could not decrypt item, no match found in known encrypted key prefixes");
                }
            }
            else
            {
                // Otherwise fallback to the type of the cached value (for non-encrypted values this will be 
                // correct). For encrypted values when not in auto-decrypt mode, this will return string type
                valueType = deserializedValue.GetType();
            }

            // Display the PlayerPref key
            EditorGUILayout.TextField(displayKey, textFieldStyle);

            // Value display and user editing
            // If we're dealing with a float
            if (valueType == typeof(float))
            {
                float initialValue;
                if (isEncryptedPair && _automaticDecryption)
                {
                    // Automatically decrypt the value if encrypted and in auto-decrypt mode
                    initialValue = PlayerPrefsUtility.GetEncryptedFloat(displayKey);
                }
                else
                {
                    // Otherwise fetch the latest plain value from PlayerPrefs in memory
                    initialValue = GetFloat(fullKey);
                }

                // Display the float editor field and get any changes in value
                float newValue = EditorGUILayout.FloatField(initialValue, textFieldStyle);

                // If the value has changed
                if (Math.Abs(newValue - initialValue) > Mathf.Epsilon)
                {
                    // Store the changed value in PlayerPrefs, encrypting if necessary
                    if (isEncryptedPair)
                    {
                        string encryptedValue = PlayerPrefsUtility.VALUE_FLOAT_PREFIX +
                                                SimpleEncryption.EncryptFloat(newValue);
                        SetString(fullKey, encryptedValue);
                    }
                    else
                    {
                        SetFloat(fullKey, newValue);
                    }

                    // Save PlayerPrefs
                    Save();
                }

                // Display the PlayerPref type
                GUILayout.Label("float", GUILayout.Width(37));
            }
            else if (valueType == typeof(int)) // if we're dealing with an int
            {
                int initialValue;
                if (isEncryptedPair && _automaticDecryption)
                {
                    // Automatically decrypt the value if encrypted and in auto-decrypt mode
                    initialValue = PlayerPrefsUtility.GetEncryptedInt(displayKey);
                }
                else
                {
                    // Otherwise fetch the latest plain value from PlayerPrefs in memory
                    initialValue = GetInt(fullKey);
                }

                // Display the int editor field and get any changes in value
                int newValue = EditorGUILayout.IntField(initialValue, textFieldStyle);

                // If the value has changed
                if (newValue != initialValue)
                {
                    // Store the changed value in PlayerPrefs, encrypting if necessary
                    if (isEncryptedPair)
                    {
                        string encryptedValue =
                            PlayerPrefsUtility.VALUE_INT_PREFIX + SimpleEncryption.EncryptInt(newValue);
                        SetString(fullKey, encryptedValue);
                    }
                    else
                    {
                        SetInt(fullKey, newValue);
                    }

                    // Save PlayerPrefs
                    Save();
                }

                // Display the PlayerPref type
                GUILayout.Label("int", GUILayout.Width(37));
            }
            else if (valueType == typeof(bool)) // if we're dealing with a bool
            {
                bool initialValue;
                if (isEncryptedPair && _automaticDecryption)
                {
                    // Automatically decrypt the value if encrypted and in auto-decrypt mode
                    initialValue = PlayerPrefsUtility.GetEncryptedBool(displayKey);
                }
                else
                {
                    // Otherwise fetch the latest plain value from PlayerPrefs in memory
                    initialValue = GetBool(fullKey);
                }

                // Display the bool toggle editor field and get any changes in value
                bool newValue = EditorGUILayout.Toggle(initialValue);

                // If the value has changed
                if (newValue != initialValue)
                {
                    // Store the changed value in PlayerPrefs, encrypting if necessary
                    if (isEncryptedPair)
                    {
                        string encryptedValue = PlayerPrefsUtility.VALUE_BOOL_PREFIX +
                                                SimpleEncryption.EncryptBool(newValue);
                        SetString(fullKey, encryptedValue);
                    }
                    else
                    {
                        SetBool(fullKey, newValue);
                    }

                    // Save PlayerPrefs
                    Save();
                }

                // Display the PlayerPref type
                GUILayout.Label("bool", GUILayout.Width(37));
            }
            else if (valueType == typeof(string)) // if we're dealing with a string
            {
                string initialValue;
                if (isEncryptedPair && _automaticDecryption && !failedAutoDecrypt)
                {
                    // Automatically decrypt the value if encrypted and in auto-decrypt mode
                    initialValue = PlayerPrefsUtility.GetEncryptedString(displayKey);
                }
                else
                {
                    // Otherwise fetch the latest plain value from PlayerPrefs in memory
                    initialValue = GetString(fullKey);
                }

                // Display the text (string) editor field and get any changes in value
                string newValue = EditorGUILayout.TextField(initialValue, textFieldStyle);

                // If the value has changed
                if (newValue != initialValue && !failedAutoDecrypt)
                {
                    // Store the changed value in PlayerPrefs, encrypting if necessary
                    if (isEncryptedPair)
                    {
                        string encryptedValue = PlayerPrefsUtility.VALUE_STRING_PREFIX +
                                                SimpleEncryption.EncryptString(newValue);
                        SetString(fullKey, encryptedValue);
                    }
                    else
                    {
                        SetString(fullKey, newValue);
                    }

                    // Save PlayerPrefs
                    Save();
                }

                if (isEncryptedPair && !_automaticDecryption && !string.IsNullOrEmpty(initialValue))
                {
                    // Because encrypted values when not in auto-decrypt mode are stored as string, determine their
                    // encrypted type and display that instead for these encrypted PlayerPrefs
                    PlayerPrefType playerPrefType = (PlayerPrefType) (int) char.GetNumericValue(initialValue[0]);
                    GUILayout.Label(playerPrefType.ToString().ToLower(), GUILayout.Width(37));
                }
                else
                {
                    // Display the PlayerPref type
                    GUILayout.Label("string", GUILayout.Width(37));
                }
            }

            // Delete button
            if (GUILayout.Button("X", GUILayout.Width(25)))
            {
                // Delete the key from PlayerPrefs
                DeleteKey(fullKey);
                // Tell Unity to Save PlayerPrefs
                Save();
                // Delete the cached record so the list updates immediately
                DeleteCachedRecord(fullKey);
            }

            EditorGUILayout.EndHorizontal();
        }

        // Calculate the padding at the bottom of the scroll view (because only visible PlayerPref rows are drawn)
        float bottomPadding = (entryCount - shownIndexLimit) * rowHeight;

        // If the padding is positive, pad the bottom so that the layout and scroll view size is correct still
        if (bottomPadding > 0)
        {
            GUILayout.Space(bottomPadding);
        }

        EditorGUILayout.EndScrollView();

        // Display the number of PlayerPrefs
        GUILayout.Label("Entry Count: " + entryCount);

        Rect rect = GUILayoutUtility.GetLastRect();
        rect.height = 1;
        rect.y -= 4;
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
    }

    private void DrawAddEntry()
    {
        // Create a GUIStyle that can be manipulated for the various text fields
        GUIStyle textFieldStyle = new GUIStyle(GUI.skin.textField);

        // Create a space
        EditorGUILayout.Space();

        // Heading
        GUILayout.Label(_showEditorPrefs ? "Add EditorPref" : "Add PlayerPref", EditorStyles.boldLabel);

        // UI for whether the new PlayerPref is encrypted and what type it is
        EditorGUILayout.BeginHorizontal();
        _newEntryIsEncrypted = GUILayout.Toggle(_newEntryIsEncrypted, "Encrypt Pref");

        if (_showEditorPrefs)
        {
            _newEntryType =
                (PlayerPrefType) GUILayout.Toolbar((int) _newEntryType,
                    new[] {"float", "int", "string", "bool"});
        }
        else
        {
            if (_newEntryType == PlayerPrefType.Bool)
                _newEntryType = PlayerPrefType.String;

            _newEntryType =
                (PlayerPrefType) GUILayout.Toolbar((int) _newEntryType, new[] {"float", "int", "string"});
        }

        EditorGUILayout.EndHorizontal();

        // Key and Value headings
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Key", EditorStyles.boldLabel);
        GUILayout.Label("Value", EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();

        // If the new value will be encrypted tint the text boxes blue (in line with the display style for existing
        // encrypted PlayerPrefs)
        if (_newEntryIsEncrypted)
        {
            if (UsingProSkin)
            {
                textFieldStyle.normal.textColor = new Color(0.5f, 0.5f, 1);
                textFieldStyle.focused.textColor = new Color(0.5f, 0.5f, 1);
            }
            else
            {
                textFieldStyle.normal.textColor = new Color(0, 0, 1);
                textFieldStyle.focused.textColor = new Color(0, 0, 1);
            }
        }

        EditorGUILayout.BeginHorizontal();

        // Track the next control so we can detect key events in it
        GUI.SetNextControlName("newEntryKey");
        // UI for the new key text box
        _newEntryKey = EditorGUILayout.TextField(_newEntryKey, textFieldStyle);

        // Track the next control so we can detect key events in it
        GUI.SetNextControlName("newEntryValue");

        switch (_newEntryType)
        {
            // Display the correct UI field editor based on what type of PlayerPref is being created
            case PlayerPrefType.Float:
            {
                _newEntryValueFloat = EditorGUILayout.FloatField(_newEntryValueFloat, textFieldStyle);
                break;
            }
            case PlayerPrefType.Int:
            {
                _newEntryValueInt = EditorGUILayout.IntField(_newEntryValueInt, textFieldStyle);
                break;
            }
            case PlayerPrefType.Bool:
            {
                _newEntryValueBool = EditorGUILayout.Toggle(_newEntryValueBool);
                break;
            }
            default:
            {
                _newEntryValueString = EditorGUILayout.TextField(_newEntryValueString, textFieldStyle);
                break;
            }
        }

        // If the user hit enter while either the key or value fields were being edited
        bool keyboardAddPressed = Event.current.isKey && Event.current.keyCode == KeyCode.Return &&
                                  Event.current.type == EventType.KeyUp &&
                                  (GUI.GetNameOfFocusedControl() == "newEntryKey" ||
                                   GUI.GetNameOfFocusedControl() == "newEntryValue");

        // If the user clicks the Add button or hits return (and there is a non-empty key), create the PlayerPref
        if ((GUILayout.Button("Add", GUILayout.Width(40)) || keyboardAddPressed) && !string.IsNullOrEmpty(_newEntryKey))
        {
            // If the PlayerPref we're creating is encrypted
            if (_newEntryIsEncrypted)
            {
                // Encrypt the key
                string encryptedKey = PlayerPrefsUtility.KEY_PREFIX + SimpleEncryption.EncryptString(_newEntryKey);

                // Note: All encrypted values are stored as string
                string encryptedValue;

                switch (_newEntryType)
                {
                    // Calculate the encrypted value
                    case PlayerPrefType.Float:
                    {
                        encryptedValue = PlayerPrefsUtility.VALUE_FLOAT_PREFIX +
                                         SimpleEncryption.EncryptFloat(_newEntryValueFloat);
                        break;
                    }
                    case PlayerPrefType.Int:
                    {
                        encryptedValue = PlayerPrefsUtility.VALUE_INT_PREFIX +
                                         SimpleEncryption.EncryptInt(_newEntryValueInt);
                        break;
                    }
                    case PlayerPrefType.Bool:
                    {
                        encryptedValue = PlayerPrefsUtility.VALUE_BOOL_PREFIX +
                                         SimpleEncryption.EncryptBool(_newEntryValueBool);
                        break;
                    }
                    default:
                    {
                        encryptedValue = PlayerPrefsUtility.VALUE_STRING_PREFIX +
                                         SimpleEncryption.EncryptString(_newEntryValueString);
                        break;
                    }
                }

                // Record the new PlayerPref in PlayerPrefs
                SetString(encryptedKey, encryptedValue);

                // Cache the addition
                CacheRecord(encryptedKey, encryptedValue);
            }
            else
            {
                switch (_newEntryType)
                {
                    case PlayerPrefType.Float:
                    {
                        // Record the new PlayerPref in PlayerPrefs
                        SetFloat(_newEntryKey, _newEntryValueFloat);
                        // Cache the addition
                        CacheRecord(_newEntryKey, _newEntryValueFloat);
                        break;
                    }
                    case PlayerPrefType.Int:
                    {
                        // Record the new PlayerPref in PlayerPrefs
                        SetInt(_newEntryKey, _newEntryValueInt);
                        // Cache the addition
                        CacheRecord(_newEntryKey, _newEntryValueInt);
                        break;
                    }
                    case PlayerPrefType.Bool:
                    {
                        // Record the new PlayerPref in PlayerPrefs
                        SetBool(_newEntryKey, _newEntryValueBool);
                        // Cache the addition
                        CacheRecord(_newEntryKey, _newEntryValueBool);
                        break;
                    }
                    default:
                    {
                        // Record the new PlayerPref in PlayerPrefs
                        SetString(_newEntryKey, _newEntryValueString);
                        // Cache the addition
                        CacheRecord(_newEntryKey, _newEntryValueString);
                        break;
                    }
                }
            }

            // Tell Unity to save the PlayerPrefs
            Save();

            // Force a repaint since hitting the return key won't invalidate layout on its own
            Repaint();

            // Reset the values
            _newEntryKey = "";
            _newEntryValueFloat = 0;
            _newEntryValueInt = 0;
            _newEntryValueString = "";

            // Deselect
            GUI.FocusControl("");
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawBottomMenu()
    {
        EditorGUILayout.Space();

        // UI for toggling automatic decryption on and off
        _automaticDecryption = EditorGUILayout.Toggle("Auto Decrypt Prefs", _automaticDecryption);

        if (_showEditorPrefs == false)
        {
            // Allow the user to import PlayerPrefs from another project (helpful when renaming product name)
            if (GUILayout.Button("Import Player Prefs From Another Unity Project"))
            {
                ImportPlayerPrefsWizard wizard =
                    ScriptableWizard.DisplayWizard<ImportPlayerPrefsWizard>("Import PlayerPrefs", "Import");
            }
        }

        EditorGUILayout.BeginHorizontal();
        float buttonWidth = (EditorGUIUtility.currentViewWidth - 10) / 2f;
        // Delete all PlayerPrefs
        if (GUILayout.Button("Delete All Preferences", GUILayout.Width(buttonWidth)))
        {
            if (EditorUtility.DisplayDialog("Delete All?", "Are you sure you want to delete all preferences?",
                "Delete All", "Cancel"))
            {
                DeleteAll();
                Save();

                // Clear the cache too, for an instant visibility update for OSX
                _deserializedPlayerPrefs.Clear();
            }
        }

        GUILayout.FlexibleSpace();

        // Mainly needed for OSX, this will encourage PlayerPrefs to save to file (but still may take a few seconds)
        if (GUILayout.Button("Force Save", GUILayout.Width(buttonWidth)))
        {
            Save();
        }

        EditorGUILayout.EndHorizontal();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();

        DrawTopBar();

        switch (Application.platform)
        {
            case RuntimePlatform.OSXEditor:
            {
                string playerPrefsPath;

                if (_showEditorPrefs)
                {
                    // From Unity Docs: On macOS, EditorPrefs are stored in ~/Library/Preferences/com.unity3d.UnityEditor.plist.
                    string majorVersion = Application.unityVersion.Split('.')[0];
                    // Construct the fully qualified path
                    playerPrefsPath =
                        Path.Combine(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "Library/Preferences"), "com.unity3d.UnityEditor" + majorVersion + ".x.plist");
                }
                else
                {
                    // From Unity Docs: On Mac OS X PlayerPrefs are stored in ~/Library/Preferences folder, in a file named unity.[company name].[product name].plist, where company and product names are the names set up in Project Settings. The same .plist file is used for both Projects run in the Editor and standalone players.

                    // Construct the plist filename from the project's settings
                    string plistFilename = $"unity.{PlayerSettings.companyName}.{PlayerSettings.productName}.plist";
                    // Now construct the fully qualified path
                    playerPrefsPath =
                        Path.Combine(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "Library/Preferences"), plistFilename);
                }

                // Determine when the plist was last written to
                DateTime lastWriteTime = File.GetLastWriteTimeUtc(playerPrefsPath);

                // If we haven't deserialized the PlayerPrefs already, or the written file has changed then deserialize 
                // the latest version
                if (!_lastDeserialization.HasValue || _lastDeserialization.Value != lastWriteTime)
                {
                    // Deserialize the actual PlayerPrefs from file into a cache
                    _deserializedPlayerPrefs =
                        new List<PlayerPrefPair>(RetrieveSavedPrefs(PlayerSettings.companyName,
                            PlayerSettings.productName));

                    // Record the version of the file we just read, so we know if it changes in the future
                    _lastDeserialization = lastWriteTime;
                }

                if (lastWriteTime != _missingDatetime)
                {
                    GUILayout.Label("Plist Last Written: " + lastWriteTime);
                }
                else
                {
                    GUILayout.Label("Plist Does Not Exist");
                }

                break;
            }
            case RuntimePlatform.WindowsEditor:
            {
                // Windows works a bit differently to OSX, we just regularly query the registry. So don't query too often
                if (!_lastDeserialization.HasValue ||
                    DateTime.UtcNow - _lastDeserialization.Value > TimeSpan.FromMilliseconds(500))
                {
                    // Deserialize the actual PlayerPrefs from registry into a cache
                    _deserializedPlayerPrefs =
                        new List<PlayerPrefPair>(RetrieveSavedPrefs(PlayerSettings.companyName,
                            PlayerSettings.productName));

                    // Record the latest time, so we don't fetch again too quickly
                    _lastDeserialization = DateTime.UtcNow;
                }

                break;
            }
        }

        EditorGUILayout.BeginVertical(GUI.skin.box);
        DrawMainList();
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical(GUI.skin.box);
        DrawAddEntry();
        EditorGUILayout.EndVertical();
        
        DrawBottomMenu();

        // If the user has scrolled, deselect - this is because control IDs within carousel will change when scrolled
        // so we'd end up with the wrong box selected.
        if (_scrollPosition != _lastScrollPosition)
        {
            // Deselect
            GUI.FocusControl("");
        }
    }

    private void CacheRecord(string key, object value)
    {
        // First of all check if this key already exists, if so replace it's value with the new value
        bool replaced = false;

        int entryCount = _deserializedPlayerPrefs.Count;
        for (int i = 0; i < entryCount; i++)
        {
            // Found the key - it exists already
            if (_deserializedPlayerPrefs[i].Key != key)
            {
                continue;
            }
            
            // Update the cached pref with the new value
            _deserializedPlayerPrefs[i] = new PlayerPrefPair() {Key = key, Value = value};
            // Mark the replacement so we no longer need to add it
            replaced = true;
            break;
        }

        // PlayerPref doesn't already exist (and wasn't replaced) so add it as new
        if (!replaced)
        {
            // Cache a PlayerPref the user just created so it can be instantly display (mainly for OSX)
            _deserializedPlayerPrefs.Add(new PlayerPrefPair() {Key = key, Value = value});
        }

        // Update the search if it's active
        UpdateSearch();
    }

    private void DeleteCachedRecord(string fullKey)
    {
        _keyQueuedForDeletion = fullKey;
    }

    // OnInspectorUpdate() is called by Unity at 10 times a second
    private void OnInspectorUpdate()
    {
        // If a PlayerPref has been specified for deletion
        if (!string.IsNullOrEmpty(_keyQueuedForDeletion))
        {
            // If the user just deleted a PlayerPref, find the ID and defer it for deletion by OnInspectorUpdate()
            if (_deserializedPlayerPrefs != null)
            {
                int entryCount = _deserializedPlayerPrefs.Count;
                for (int i = 0; i < entryCount; i++)
                {
                    if (_deserializedPlayerPrefs[i].Key != _keyQueuedForDeletion)
                    {
                        continue;
                    }
                    
                    _deserializedPlayerPrefs.RemoveAt(i);
                    break;
                }
            }

            // Remove the queued key since we've just deleted it
            _keyQueuedForDeletion = null;

            // Update the search results and repaint the window
            UpdateSearch();
            Repaint();
        }
        else if (_inspectorUpdateFrame % 10 == 0) // Once a second (every 10th frame)
        {
            // Force the window to repaint
            Repaint();
        }

        // Track what frame we're on, so we can call code less often
        _inspectorUpdateFrame++;
    }

    public void Import(string companyName, string productName)
    {
        // Walk through all the imported PlayerPrefs and apply them to the current PlayerPrefs
        IEnumerable<PlayerPrefPair> importedPairs = RetrieveSavedPrefs(companyName, productName);
        foreach (PlayerPrefPair importedPair in importedPairs)
        {
            Type type = importedPair.Value.GetType();
            if (type == typeof(float))
                SetFloat(importedPair.Key, (float) importedPair.Value);
            else if (type == typeof(int))
                SetInt(importedPair.Key, (int) importedPair.Value);
            else if (type == typeof(string))
                SetString(importedPair.Key, (string) importedPair.Value);

            // Cache any new records until they are reimported from disk
            CacheRecord(importedPair.Key, importedPair.Value);
        }

        // Force a save
        Save();
    }

    private static bool UsingProSkin
    {
        get
        {
            #if UNITY_3_4
			if(EditorPrefs.GetInt("UserSkin") == 1)		
			{
				return true;
			}
			else
			{
				return false;
			}
            #else
            return EditorGUIUtility.isProSkin;
            #endif
        }
    }
}