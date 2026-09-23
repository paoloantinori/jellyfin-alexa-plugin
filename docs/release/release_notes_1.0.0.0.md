# Jellyfin Alexa Skill 1.0.0.0

The first major release of the Alexa skill for Jellyfin: music, podcasts, audiobooks, movies and series on your Echo devices, in 17 languages.

## Works on Jellyfin 10.11 and Jellyfin 12

One release, two downloads: the plugin catalog picks the right one for your server automatically. If you install manually, use the `.net10.zip` on Jellyfin 12 servers and the plain zip on 10.11.

## Sleep timers that understand you

Ask to stop playback after thirty seconds, half an hour or one hour, and that is exactly what gets set. The confirmation tells the truth: when the stop will land at the end of the current track (a platform timing limit), the skill says so instead of promising a mid-song stop it cannot deliver.

## Reliable resume

Saying "resume" now continues what you last listened to on that device, music, podcast or audiobook, instead of occasionally jumping to something unrelated from days earlier. Opening the skill offers the same item a bare resume would pick, so the two never disagree.

## Transport commands that answer

Shuffle, repeat and loop now confirm out loud when they take effect (before: silence that looked like nothing happened). Shuffle can be asked in Italian ("mescola la coda"), German ("Zufallswiedergabe aktivieren") and French ("mélange la file d'attente").

## Radio that does not restart your song

"Play similar tracks" now starts a different track instead of restarting the one you are halfway through. When a queue ends, autoplay keeps the music going with similar material from your library.

## Smoother conversations

- If you ask for a new command while the skill is waiting for an answer (for example a song title for a playlist), it cancels the question cleanly and tells you to repeat the command, instead of answering nonsense.
- Video requests work without a title first: ask to "play a film" and the skill asks which one; naming the film in one shot works too.
- Artist information no longer reads punctuation aloud when listing genres.

## Known limitations

- Bare transport words (stop, next) during playback are frequently claimed by the device's default music service; this is an Amazon platform limitation, tracked upstream. Say commands with the skill name for reliable routing.
- The sleep timer stops at the nearest track boundary, not mid-song (the platform sends no mid-track events).
- Custom skills get no seek bar on music playback; audiobooks played on Echo Show devices do get one.

## For existing users

Your plugin configuration (users, linked accounts, settings) survives the update. If you install manually on Jellyfin 12, make sure to use the `.net10.zip`.
