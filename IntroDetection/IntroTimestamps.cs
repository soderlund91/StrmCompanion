namespace StrmCompanion.IntroDetection
{
    public class IntroTimestamps
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double LengthSeconds => EndSeconds - StartSeconds;
    }
}
