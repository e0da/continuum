fn main() {
    let result = (|| {
        let p = continuum_numerics::output_argument()?;
        continuum_numerics::write_json_exclusive(
            &p,
            &continuum_numerics::field::trajectory_report(),
        )
    })();
    if let Err(e) = result {
        eprintln!("field-trajectory: {e}");
        std::process::exit(1)
    }
}
