#
# The icon set Counter uses, and the exact upstream release it comes from.
#
# This file is data only. Sync-FluentIcons.ps1 reads it, downloads exactly these files from the
# pinned tag, verifies them against Assets/Icons/Fluent/manifest.json, and regenerates
# Theme/Icons.xaml and Controls/IconCatalog.g.cs from them. Nothing else in the build touches
# the network, and nothing follows a moving branch: Revision below is a release tag and Commit
# is the exact commit that tag pointed at when the assets were taken.
#
# Adding an icon means adding a row here and re-running the script. Hand-editing the generated
# files is pointless - the next run overwrites them.
#
@{
    Source   = 'https://github.com/microsoft/fluentui-system-icons'
    Revision = '1.1.339'
    Commit   = '4d685f77b2cb8f3f412a74ec8d920c8c91149528'
    License  = 'MIT'

    # Kind is the IconKind member. Variant is Regular or Filled. Folder and File are the
    # upstream asset path. Size is the source viewBox edge, preserved rather than normalised.
    Icons    = @(
        @{ Kind = 'Add';               Variant = 'Regular'; Folder = 'Add';                     File = 'ic_fluent_add_20_regular.svg';                     Size = 20 }
        @{ Kind = 'ArrowDownload';     Variant = 'Regular'; Folder = 'Arrow Download';          File = 'ic_fluent_arrow_download_20_regular.svg';          Size = 20 }
        @{ Kind = 'ArrowExport';       Variant = 'Regular'; Folder = 'Arrow Export';            File = 'ic_fluent_arrow_export_20_regular.svg';            Size = 20 }
        @{ Kind = 'ArrowReset';        Variant = 'Regular'; Folder = 'Arrow Reset';             File = 'ic_fluent_arrow_reset_20_regular.svg';             Size = 20 }
        @{ Kind = 'ArrowUpload';       Variant = 'Regular'; Folder = 'Arrow Upload';            File = 'ic_fluent_arrow_upload_20_regular.svg';            Size = 20 }
        @{ Kind = 'Calendar';          Variant = 'Regular'; Folder = 'Calendar LTR';            File = 'ic_fluent_calendar_ltr_20_regular.svg';            Size = 20 }
        @{ Kind = 'CalendarEmpty';     Variant = 'Regular'; Folder = 'Calendar Empty';          File = 'ic_fluent_calendar_empty_20_regular.svg';          Size = 20 }
        @{ Kind = 'CalendarToday';     Variant = 'Regular'; Folder = 'Calendar Today';          File = 'ic_fluent_calendar_today_20_regular.svg';          Size = 20 }
        @{ Kind = 'Checkmark';         Variant = 'Regular'; Folder = 'Checkmark';               File = 'ic_fluent_checkmark_12_regular.svg';               Size = 12 }
        @{ Kind = 'CheckmarkCircle';   Variant = 'Filled';  Folder = 'Checkmark Circle';        File = 'ic_fluent_checkmark_circle_20_filled.svg';         Size = 20 }
        @{ Kind = 'ChevronDown';       Variant = 'Regular'; Folder = 'Chevron Down';            File = 'ic_fluent_chevron_down_20_regular.svg';            Size = 20 }
        @{ Kind = 'ChevronLeft';       Variant = 'Regular'; Folder = 'Chevron Left';            File = 'ic_fluent_chevron_left_20_regular.svg';            Size = 20 }
        @{ Kind = 'ChevronRight';      Variant = 'Regular'; Folder = 'Chevron Right';           File = 'ic_fluent_chevron_right_20_regular.svg';           Size = 20 }
        @{ Kind = 'ChevronUp';         Variant = 'Regular'; Folder = 'Chevron Up';              File = 'ic_fluent_chevron_up_20_regular.svg';              Size = 20 }
        @{ Kind = 'Circle';            Variant = 'Filled';  Folder = 'Circle';                  File = 'ic_fluent_circle_20_filled.svg';                   Size = 20 }
        @{ Kind = 'ClipboardTaskList'; Variant = 'Regular'; Folder = 'Clipboard Task List LTR'; File = 'ic_fluent_clipboard_task_list_ltr_20_regular.svg'; Size = 20 }
        @{ Kind = 'Clock';             Variant = 'Regular'; Folder = 'Clock';                   File = 'ic_fluent_clock_20_regular.svg';                   Size = 20 }
        @{ Kind = 'Color';             Variant = 'Regular'; Folder = 'Color';                   File = 'ic_fluent_color_20_regular.svg';                   Size = 20 }
        @{ Kind = 'DataBarVertical';   Variant = 'Regular'; Folder = 'Data Bar Vertical';       File = 'ic_fluent_data_bar_vertical_20_regular.svg';       Size = 20 }
        @{ Kind = 'Database';          Variant = 'Regular'; Folder = 'Database';                File = 'ic_fluent_database_20_regular.svg';                Size = 20 }
        @{ Kind = 'Delete';            Variant = 'Regular'; Folder = 'Delete';                  File = 'ic_fluent_delete_20_regular.svg';                  Size = 20 }
        @{ Kind = 'Desktop';           Variant = 'Regular'; Folder = 'Desktop';                 File = 'ic_fluent_desktop_20_regular.svg';                 Size = 20 }
        @{ Kind = 'Dismiss';           Variant = 'Regular'; Folder = 'Dismiss';                 File = 'ic_fluent_dismiss_20_regular.svg';                 Size = 20 }
        @{ Kind = 'DismissCircle';     Variant = 'Regular'; Folder = 'Dismiss Circle';          File = 'ic_fluent_dismiss_circle_20_regular.svg';          Size = 20 }
        @{ Kind = 'Edit';              Variant = 'Regular'; Folder = 'Edit';                    File = 'ic_fluent_edit_20_regular.svg';                    Size = 20 }
        @{ Kind = 'ErrorCircle';       Variant = 'Filled';  Folder = 'Error Circle';            File = 'ic_fluent_error_circle_20_filled.svg';             Size = 20 }
        @{ Kind = 'Fire';              Variant = 'Regular'; Folder = 'Fire';                    File = 'ic_fluent_fire_20_regular.svg';                    Size = 20 }
        @{ Kind = 'Fire';              Variant = 'Filled';  Folder = 'Fire';                    File = 'ic_fluent_fire_20_filled.svg';                     Size = 20 }
        @{ Kind = 'Folder';            Variant = 'Regular'; Folder = 'Folder';                  File = 'ic_fluent_folder_20_regular.svg';                  Size = 20 }
        @{ Kind = 'MoreHorizontal';    Variant = 'Regular'; Folder = 'More Horizontal';         File = 'ic_fluent_more_horizontal_20_regular.svg';         Size = 20 }
        @{ Kind = 'Note';              Variant = 'Regular'; Folder = 'Note';                    File = 'ic_fluent_note_20_regular.svg';                    Size = 20 }
        @{ Kind = 'Options';           Variant = 'Regular'; Folder = 'Options';                 File = 'ic_fluent_options_20_regular.svg';                 Size = 20 }
        @{ Kind = 'PaintBrush';        Variant = 'Regular'; Folder = 'Paint Brush';             File = 'ic_fluent_paint_brush_20_regular.svg';             Size = 20 }
        @{ Kind = 'Pause';             Variant = 'Filled';  Folder = 'Pause';                   File = 'ic_fluent_pause_20_filled.svg';                    Size = 20 }
        @{ Kind = 'Pin';               Variant = 'Regular'; Folder = 'Pin';                     File = 'ic_fluent_pin_20_regular.svg';                     Size = 20 }
        @{ Kind = 'Pin';               Variant = 'Filled';  Folder = 'Pin';                     File = 'ic_fluent_pin_20_filled.svg';                      Size = 20 }
        @{ Kind = 'Play';              Variant = 'Filled';  Folder = 'Play';                    File = 'ic_fluent_play_20_filled.svg';                     Size = 20 }
        @{ Kind = 'Save';              Variant = 'Regular'; Folder = 'Save';                    File = 'ic_fluent_save_20_regular.svg';                    Size = 20 }
        @{ Kind = 'Settings';          Variant = 'Regular'; Folder = 'Settings';                File = 'ic_fluent_settings_20_regular.svg';                Size = 20 }
        @{ Kind = 'Sparkle';           Variant = 'Regular'; Folder = 'Sparkle';                 File = 'ic_fluent_sparkle_20_regular.svg';                 Size = 20 }
        @{ Kind = 'Speaker2';          Variant = 'Regular'; Folder = 'Speaker 2';               File = 'ic_fluent_speaker_2_20_regular.svg';               Size = 20 }
        @{ Kind = 'SpeakerOff';        Variant = 'Regular'; Folder = 'Speaker Off';             File = 'ic_fluent_speaker_off_20_regular.svg';             Size = 20 }
        @{ Kind = 'Stop';              Variant = 'Filled';  Folder = 'Stop';                    File = 'ic_fluent_stop_20_filled.svg';                     Size = 20 }
        @{ Kind = 'Timer';             Variant = 'Regular'; Folder = 'Timer';                   File = 'ic_fluent_timer_20_regular.svg';                   Size = 20 }
        @{ Kind = 'Warning';           Variant = 'Filled';  Folder = 'Warning';                 File = 'ic_fluent_warning_20_filled.svg';                  Size = 20 }
        @{ Kind = 'WeatherMoon';       Variant = 'Regular'; Folder = 'Weather Moon';            File = 'ic_fluent_weather_moon_20_regular.svg';            Size = 20 }
        @{ Kind = 'WeatherSunny';      Variant = 'Regular'; Folder = 'Weather Sunny';           File = 'ic_fluent_weather_sunny_20_regular.svg';           Size = 20 }
    )
}
