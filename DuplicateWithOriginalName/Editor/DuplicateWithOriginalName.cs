using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityToolbag
{
    public static class DuplicateWithOriginalName
    {
        private const string MenuItemName = "Edit/Duplicate With Original Name %#d";

        [MenuItem(MenuItemName)]
        public static void Duplicate()
        {
            var newSelection = new List<GameObject>();

            foreach (var origObj in Selection.gameObjects)
            {
                var newObj = Object.Instantiate(origObj);
                newObj.transform.SetParent(origObj.transform.parent);

                // If this object is a prefab instance we need to connect the new object to the prefab
                // and copy over all property modifications so that the instances are identical.
                var prefabType = PrefabUtility.GetPrefabType(origObj);
                if (prefabType == PrefabType.PrefabInstance ||
                    prefabType == PrefabType.ModelPrefabInstance)
                {
                    var prefab = PrefabUtility.GetPrefabParent(origObj) as GameObject;
                    newObj = PrefabUtility.ConnectGameObjectToPrefab(newObj, prefab);
                    PrefabUtility.SetPropertyModifications(newObj, PrefabUtility.GetPropertyModifications(origObj));
                }

                newObj.name = origObj.name;
                newSelection.Add(newObj);

                Undo.RegisterCreatedObjectUndo(newObj, "Duplicate With Original Name");
            }

            Selection.objects = newSelection.Cast<Object>().ToArray();
        }

        [MenuItem(MenuItemName, true)]
        public static bool CanDuplicate()
        {
            if (Selection.objects.Length == 0)
                return false;

            // Don't use our custom duplicate on any assets
            return
                Selection.objects.All(
                    obj =>
                        !AssetDatabase.IsMainAsset(obj) &&
                        !AssetDatabase.IsForeignAsset(obj) &&
                        !AssetDatabase.IsNativeAsset(obj) &&
                        !AssetDatabase.IsSubAsset(obj));
        }
    }
}
