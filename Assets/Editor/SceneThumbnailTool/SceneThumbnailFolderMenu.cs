using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaykerStudio.SceneThumbnails
{
    /// <summary>
    /// Searchable folder picker shown by SceneThumbnailWindow's Browse button.
    /// Lightweight drop-down EditorWindow (Add-Component style): a search field
    /// on top and one button per project folder below. Anchored with
    /// ShowAsDropDown so it closes on outside click; also closes on selection
    /// or Escape. Reuses SceneThumbnailWindow.uss for row/search styling.
    /// </summary>
    public class SceneThumbnailFolderMenu : EditorWindow
    {
        #region Constants

        private const string k_UssPath =
            "Assets/Editor/SceneThumbnailTool/SceneThumbnailWindow.uss";
        private const string k_SearchPlaceholder = "Search folders...";
        private const string k_NoMatchLabel = "No folders match";
        private const string k_EmptyListLabel = "No folders";
        private const float k_MenuWidth = 340f;
        private const float k_MenuHeight = 260f;

        #endregion

        #region Fields

        private TextField _searchField;
        private Label _placeholder;
        private ScrollView _list;
        private IReadOnlyList<string> _folderPaths;
        private Action<string> _onSelected;

        #endregion

        #region Public Methods

        /// <summary>
        /// Opens the searchable folder menu anchored below the given screen
        /// rect. folderPaths is a flat project-relative list (e.g.
        /// "Assets/Editor"); null/empty shows a "No folders" row.
        /// </summary>
        public static void ShowMenu(
            Rect buttonScreenRect,
            IReadOnlyList<string> folderPaths,
            Action<string> onSelected
        )
        {
            SceneThumbnailFolderMenu window = CreateInstance<SceneThumbnailFolderMenu>();
            window.titleContent = new GUIContent("Select Scene Folder");
            window._folderPaths = folderPaths;
            window._onSelected = onSelected;
            window.ShowAsDropDown(buttonScreenRect, new Vector2(k_MenuWidth, k_MenuHeight));
        }

        #endregion

        #region Unity Callbacks

        private void CreateGUI()
        {
            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_UssPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            VisualElement searchBox = new VisualElement();
            searchBox.AddToClassList("stt-folder-picker-search");
            rootVisualElement.Add(searchBox);

            _searchField = new TextField();
            _searchField.name = "folder-picker-search-input";
            _searchField.RegisterValueChangedCallback(evt =>
            {
                UpdatePlaceholder();
                RefreshRows(evt.newValue);
            });
            searchBox.Add(_searchField);

            _placeholder = new Label(k_SearchPlaceholder);
            _placeholder.pickingMode = PickingMode.Ignore;
            _placeholder.AddToClassList("stt-folder-picker-placeholder");
            searchBox.Add(_placeholder);

            _list = new ScrollView();
            _list.name = "folder-picker-list";
            _list.AddToClassList("stt-folder-picker-list");
            rootVisualElement.Add(_list);

            rootVisualElement.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape)
                {
                    return;
                }
                evt.StopPropagation();
                Close();
            });

            UpdatePlaceholder();
            RefreshRows(string.Empty);
            rootVisualElement.schedule.Execute(() => _searchField.Focus()).ExecuteLater(0);
        }

        #endregion

        #region Private Methods

        private void UpdatePlaceholder()
        {
            bool hasText = _searchField != null && !string.IsNullOrEmpty(_searchField.value);
            if (_placeholder != null)
            {
                _placeholder.EnableInClassList("stt-hidden", hasText);
            }
        }

        private void RefreshRows(string filter)
        {
            if (_list == null)
            {
                return;
            }
            _list.Clear();
            string query = filter ?? string.Empty;
            bool hasQuery = query.Length > 0;
            int matched = 0;
            if (_folderPaths != null)
            {
                for (int i = 0; i < _folderPaths.Count; i++)
                {
                    string path = _folderPaths[i];
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    if (hasQuery && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    Button row = new Button();
                    row.AddToClassList("stt-folder-row");
                    row.text = path;
                    string capturedPath = path;
                    row.clicked += () => OnRowClicked(capturedPath);
                    _list.Add(row);
                    matched++;
                }
            }
            if (matched == 0)
            {
                Label emptyLabel = new Label(hasQuery ? k_NoMatchLabel : k_EmptyListLabel);
                emptyLabel.AddToClassList("stt-empty-label");
                emptyLabel.SetEnabled(false);
                _list.Add(emptyLabel);
            }
        }

        private void OnRowClicked(string path)
        {
            Close();
            if (_onSelected != null)
            {
                _onSelected(path);
            }
        }

        #endregion
    }
}
