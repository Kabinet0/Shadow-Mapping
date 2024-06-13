# Shadow Mapping
An implementation of Variance Shadow Mapping in Unity's URP.

 - For experimental purposes only
 - Limited to punctual lights only (No directional support, would require EVSM to look good)
 - Uses a moving average box blur compute shader to blur point light shadow cubemaps (with support for blurring over edges), and a standard pixel box blur for spotlights
 - In short, thoroughly unfinished, though perhaps cool to look at
