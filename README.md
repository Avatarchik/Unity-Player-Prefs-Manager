# Unity Player Prefs Manager

Player Prefs Manager provides an easy way to see what Player & Editor Prefs your game has and change/add/remove them during edit-mode or at run-time.

It also includes encryption support to protect your prefs from casual hacking and has additional support for more data types.

This repository was originally created by [Sabresaurus](https://github.com/sabresaurus), the original repository can be found [here](https://github.com/sabresaurus/PlayerPrefsEditor). This version contains many bug fixes, updates for newer versions of Unity and some minor code enhancements & improvements.

Editor features include:
- List all Player/Editor Prefs
- Search for Player/Editor Prefs to refine results
- Change Player/Editor Prefs values at run-time
- Add new Player/Editor Prefs
- Delete Player/Editor Prefs
- Delete all button
- Import Player Prefs from another project
- Supports working with the encryption features added in the utilities

Utilities features include:
- Set and get the built in Player/Editor Pref types using an encryption layer - plain text values are transparently converted to encryption so that the Player/Editor Prefs are protected in the device data stores
- Set and get Enum values
- Set and get DateTime values
- Set and get TimeSpan values
- Set and get Bool values


## Player Prefs Manager

To open the Players Prefs Manager go to Unity Tools -> Player Prefs Manager

This will open an editor window which you can dock like any other Unity window.

### The Prefs List

If you have existing saved prefs you should see them listed in the main window. This window shows the prefs Key, its Value and its data Type. You can change the values just by changing the value text box, you can also delete one of these existing prefs by clicking the 'X' button on the right.

### Search

The editor supports filtering keys by entering a keyword in the search textbox at the top. As you type the search results will refine. Search is case-insensitive and if 'auto decrypt pref' is turned on it will also work with encrypted prefs.

### Adding A New Player Pref

At the bottom of the editor you'll see a section for adding a new pref. There are toggle options to determine what type it is and a checkbox for whether the new pref should be encrypted. Once you've selected the right settings and filled in a key and value hit the Add button to instantly add the player pref.

## Player Prefs Utilities & Encryption

IMPORTANT: If using encryption, make sure you change the key specified in `SimpleEncryption.cs`, this will make sure your key is unique and make the protection stronger.

In `PlayerPrefsUtility.cs` you'll find a set of utility methods for dealing with encryption and also new data types. There is documentation within this file explaining how each method works.
