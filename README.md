<img width="996" height="560" alt="strmcompanion" src="https://github.com/user-attachments/assets/5c0cd6c5-6d3c-4915-9466-669832748fd4" />



This plugin enables some functions that is missing from Emby when using .strm files. Most of the functions can also be used on regular media types. 

## Media info extract
This plugin probes selected libraries to extract the media information. 
For example: resolution, playtime, audio-codec, subtitles, etc.

## Intro dectect
The built in Emby function to detect intros can not run on .strm files. This plugin uses the same technique as Emby (chromaprint) and some other tweaks to find the intro. 
It then posts the intro markers to Emby database and it will work as native in Emby player.

## Auto merge
The plugin can merge same movies across multiple libraries and folders. Emby native can only merge if they are located within the same folder. This enable you to have a folder of 4K movies and another one of 1080p.
The plugin then finds multiple movies (or episodes) with the same IMDb or TVDb ID and merges them. 
