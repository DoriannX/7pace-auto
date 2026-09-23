// Sans console en version publiée : l'app n'a que ses deux fenêtres.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    septpaceauto_app_lib::run()
}
