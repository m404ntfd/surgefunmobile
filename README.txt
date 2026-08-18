SURGE GUEST INFORMATION KIOSK FOR WINDOWS
=========================================

This repository builds the full-screen Windows version of the original Surge
Guest Information Kiosk for the Mobile, Alabama location.

Source repository and automatic update feed:

https://github.com/m404ntfd/surgefunmobile

Latest Windows installer:

https://github.com/m404ntfd/surgefunmobile/releases/latest


THE GUEST EXPERIENCE
--------------------

The application contains the original touch-friendly kiosk experience:

* Surge Mobile event information and guest contact details.
* Food and beverage preferences.
* Attraction information for Laser Tag, Mini Golf, Sports Simulators, Ropes
  Course, Go Karting, XD Dark Ride, Bowling, Pickleball, Strike Arena, and
  party rooms.
* Activity selection and a complete on-screen review.
* Official Surge logo and colors.

The complete guest experience is built into the application. It does not rely
on a separate website being available, so guests can continue viewing and
preparing requests if the internet connection is temporarily unavailable.


QUICK INSTALL
-------------

1. Open the latest-release link above.
2. Download SurgeMobile.EventKiosk-win-Setup.exe.
3. Exit an older copy with Ctrl + Alt + Shift + F12.
4. Run the Setup file.
5. On a new computer, create a 4-8 digit numerical staff password.

The optional Install-Surge-Kiosk.cmd helper can also create a Windows startup
shortcut.


AUTOMATIC UPDATES
-----------------

The kiosk checks this repository's public GitHub Releases feed whenever it
starts. If a newer release is available, it downloads the update, installs it,
and restarts before the guest screen opens.

Staff can also press Ctrl + Alt + Shift + F12, open Staff Settings, and select
"Check for kiosk update."

Every push to the main branch runs the included GitHub Actions workflow. The
workflow builds a self-contained Windows application, creates the next version,
and publishes the Velopack installer and update packages.


STAFF SETTINGS
--------------

Press all four keys together:

Ctrl + Alt + Shift + F12

After entering the staff password, staff can:

* Return to the kiosk.
* Reset the guest screen and clear the current on-screen request.
* Display or remove the Guest Information Kiosk Closed page.
* Check for and install a GitHub update.
* Change the staff password.
* Exit the application.

The closed setting remains in effect after the kiosk or computer restarts. The
staff shortcut continues to work while the closed page is displayed.


KIOSK PROTECTION
----------------

* Borderless, full-screen display.
* Password-protected staff settings.
* External navigation, pop-ups, downloads, browser menus, developer tools, and
  normal browser shortcuts are disabled.
* The guest screen automatically resets after three minutes without activity.
* The current guest's entries are cleared during every reset.


RESET A FORGOTTEN STAFF PASSWORD
--------------------------------

1. Exit the kiosk.
2. Right-click Reset-Staff-PIN.ps1 and choose "Run with PowerShell."
3. Confirm the reset.
4. Start the kiosk and create a new staff password.


WINDOWS REQUIREMENTS
--------------------

* Windows 10 or Windows 11, 64-bit
* Microsoft Edge WebView2 Runtime
* Internet connection for automatic application updates

The installed application is self-contained and does not require the .NET SDK.


PUBLISHING AN UPDATE
--------------------

1. Make and test the kiosk change.
2. Commit and push the change to main.
3. Wait for the "Publish Guest Information Kiosk Update" workflow to finish.
4. Restart a kiosk or use "Check for kiosk update" in Staff Settings.

The existing internal application and package names are intentionally retained
so computers already running version 1.0.2 can update to this guest-information
edition automatically.
