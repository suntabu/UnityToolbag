This editor script adds a new menu item under Edit that can duplicate objects in the scene while retaining their original names.

By default the Unity editor changes the names of duplicated objects to ensure they are unique. The [reason given](https://forum.unity3d.com/threads/how-do-i-stop-unity-5-from-changing-my-object-names-when-adding-them-to-the-scene.299644/#post-1976581) was that the animation system uses game object name/hierarchy paths as part of retargeting. As such it makes sense that the editor does the safe thing by default by creating new objects with unique names.

This script adds a new menu item called "Edit/Duplicate with Original Name" that duplicates your selection and replaces the new object names with the names of the original objects they copied. It also maintains links to prefabs and changes made to the instances of those prefabs. In general it _should_ work the same as regular duplicate for objects in the scene (though I've not tested in every situation so there might be something I missed).

The menu item is bound to `ctrl-shift-d` on Windows/Linux and `cmd-shift-d` on macOS for easy access.
