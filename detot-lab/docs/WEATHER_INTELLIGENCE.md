# Weather Intelligence

Weather is the first proactive source. No Memory, Calendar, Gmail, Drive, device state, or chat history is read by this feature.

## Consent gates

The worker and backend routes require all three owner-scoped grants for `public_weather / proactive_analysis`:

- `local_only`: read and store the selected city, forecast cache, and suggestions in an encrypted owner-specific file.
- `external_service`: send the city name to Open-Meteo geocoding and the resulting coordinates to its forecast endpoint.
- `DeepSeek`: send only opaque candidate IDs, forecast times, precipitation probability, and precipitation amounts to DeepSeek. The city name and coordinates are omitted.

All grants default to denied. OAuth does not participate. Revoking any weather grant cancels active work, clears the encrypted local file (location, forecast, cache, suggestions, and feedback), and blocks subsequent requests. Clearing the location is also available as a direct local delete action.

Feedback has an independent `proactive_feedback / skill_learning / local_only` grant. Without both that grant and the weather local grant, the backend rejects feedback. Counters and dismissed alert IDs use a separate encrypted file, keyed to the feedback grant's current change marker; a stale copy cannot be restored after revocation and regrant. Feedback is not sent to DeepSeek. Dismissal suppresses that same forecast alert. After three dismissals of rain nudges, the local rule suppresses future candidates below 90% probability; candidates at or above 90% remain eligible. Interest counts are stored locally for future ranking; they do not train a model in this version.

## Forecast and suggestion rules

The user enters a city or area explicitly. The app requests a short list of geocoding matches and uses the first valid match. It requests two days of hourly precipitation probability and amounts. Forecasts are validated before use and cached encrypted for 30 minutes; a background check runs every 10 minutes only while consent is valid. API errors do not produce a suggestion. Error logs contain exception types only.

The local rule requires three consecutive forecast hours with at least 70% precipitation probability and either one hour with at least 5 mm precipitation or a three-hour total of at least 15 mm. The model may select zero, one, or two eligible windows and choose a bounded precaution. The backend discards unknown candidate IDs and any result outside the permitted choices. It computes the reason, time window, probability, and certainty label from the forecast rather than trusting model-written measurements. If nothing meets the rule or DeepSeek selects nothing, the app stays quiet.

Suggestions link to Open-Meteo's forecast documentation and show the provider, forecast retrieval time, local time window, precipitation probability, and a derived certainty label. They are practical heads-up messages, not official severe-weather warnings. Forecasts are uncertain and can change.

## Provider terms

The implementation uses Open-Meteo's Geocoding API and Forecast API. Its free/open-access API is limited to non-commercial use and requires attribution under CC BY 4.0. Review the current [terms](https://open-meteo.com/en/terms), [geocoding documentation](https://open-meteo.com/en/docs/geocoding-api), [forecast documentation](https://open-meteo.com/en/docs), and [license](https://open-meteo.com/en/license) before any commercial deployment.
