# Template hints for rhapsody-core

Drop-in for the Rhapsody core repo (github.com/arout77/rhapsody-core), used by Rhapsody Server's **Template hints** feature.

1. Copy `src/Twig/*` into rhapsody-core's `src/Twig/`.
2. In `src/bootstrap.php`, right after `$twig = new Environment($loader, $twigOptions);` add:

   ```php
   \Rhapsody\Core\Twig\TemplateHintsExtension::register($twig, $isDevelopment);
   ```

It does nothing unless the app is in development mode **and** `RHAPSODY_TEMPLATE_HINTS` is set (Rhapsody Server sets it per site). It never writes markers in CLI, for non-HTML responses, or inside email/text/JSON/CSS/JS templates.
