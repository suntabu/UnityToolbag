using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

namespace UnityEditor.UI{
	[CustomEditor(typeof(UnityEngine.UI.ExtendImage), true)]
	public class ExtendImageEditor : ImageEditor {

		public override void OnInspectorGUI ()
		{
			base.OnInspectorGUI ();

			serializedObject.Update();
			EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Extend"), true);
			serializedObject.ApplyModifiedProperties();
		}


		[MenuItem("GameObject/UI/Extend Image")]
		public static void CreateExtendImage()
		{
			var selection = Selection.activeGameObject;
			if (!selection)
			{
				var canvas = FindObjectOfType<Canvas>();
				if (canvas == null)
				{
					canvas = new GameObject("Canvas").AddComponent<Canvas>();
					canvas.gameObject.AddComponent<CanvasScaler>();
					canvas.gameObject.AddComponent<GraphicRaycaster>();
				}
				selection = canvas.gameObject;
			}
			
			var img = new GameObject("ExtendImage").AddComponent<ExtendImage>();
			img.transform.SetParent(selection.transform);
		}
	}
}