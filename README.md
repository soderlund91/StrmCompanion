<img width="498" height="280" alt="strmcompanion_dark_small" src="https://github.com/user-attachments/assets/4298a45a-273b-4cf7-be7a-d340b499d720" />


This plugin enables functions that is missing from native Emby when using .strm files. Most of the functions can also be used on regular media types(mkv, avi, etc).


# Features
## Media info extract
##### Specific for .strm files
This plugin uses FFmpeg to probe movies and episodes in selected libraries to extract the media information. 
This enable Emby to recognize resolution, playtime, audio-codec, subtitles, etc that Emby natively cannot do. 
It also enables for faster startup since this process has already been made.  

Can be run as a scheduled task and/or auto detect on new media. 

## Auto merge
##### For all media types
The plugin can merge same movies and episodes across multiple libraries and folders. Emby native only merge if they are located within the same folder. This enable you to for example  have a folder of 4K movies and another one of 1080p. The plugin then finds multiple movies or episodes with the same IMDb or TVDb ID and merges them across all or selected libraries. 

Can be run as a scheduled task and/or auto detect on new media. 

## Intro dectect
##### For all media types
The built in Emby function to detect intros can not run on .strm files. This plugin uses the same technique as Emby (chromaprint) and some other tweaks to find the intro. The detection runs on user selected shows or season, it then posts the intro markers to Emby database and it will work as native in Emby player with the "skip intro" button.

The intro detect also can list all existing intro markers even if they are not from this plugin. 


> [!NOTE]
> Check each release for required server version.
The intro detection is work in progress. It currently works really good on some shows, and not as good on others. The process is pretty slow (I have prioritized quality over speed) but when the fingerprinting is done you can change the settings and play around with them and it will be a lot quicker. The fingerprinting is the slow process and unaffected by the user settings. 
So do the fingerprinting and then test with other settings.
