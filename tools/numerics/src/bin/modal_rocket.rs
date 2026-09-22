fn main() {
    let result = (|| {
        let path = continuum_numerics::output_argument()?;
        if !path.parent().is_some_and(|p| p.is_dir()) {
            return Err("output parent does not exist".into());
        }
        let r = continuum_numerics::modal::report();
        continuum_numerics::write_json_exclusive(&path, &r)?;
        println!("Wrote {} to {}", r.experiment, path.display());
        if !r.qualified {
            std::process::exit(2)
        }
        Ok::<_, String>(())
    })();
    if let Err(e) = result {
        eprintln!("modal-rocket: {e}");
        std::process::exit(1)
    }
}
